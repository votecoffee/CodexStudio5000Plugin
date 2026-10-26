"""Opt-in selected-path frontdoor fixtures. No real service/controller mutation."""
import ctypes
from ctypes import wintypes as w
from contextlib import ExitStack
import hashlib
import json
import os
import queue
import shutil
import subprocess
import threading
import time
import uuid
from pathlib import Path
import pytest
from universal_tools.resources import ResourceLedger
from test_offline_bootstrap import fixture, ROOT, POWERSHELL

PAYLOAD = Path(os.environ.get("ROCKWELL_FRONTDOOR_PAYLOAD", str(ROOT / "artifacts/test-tools/frontdoor-prep")))
COHORT = PAYLOAD.with_name(PAYLOAD.name + "-cohort.json")
pytestmark = pytest.mark.skipif(os.name != "nt" or os.environ.get("ROCKWELL_DOTNET_RESOURCE_PROCESSES") != "1",
                               reason="Explicit opt-in for isolated selected-path process fixtures")

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

class Memory(ctypes.Structure):
    _fields_ = [("cb", w.DWORD), ("faults", w.DWORD)] + [(name, ctypes.c_size_t) for name in
        ["peakWorkingSet", "workingSet", "quotaPeakPaged", "quotaPaged", "quotaPeakNonPaged", "quotaNonPaged",
         "pagefile", "peakPagefile", "privateBytes"]]

def observe(driver, identity):
    with driver._open(identity["pid"]) as handle:
        assert handle.identity == identity and handle.alive()
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.K32GetProcessMemoryInfo.argtypes = [w.HANDLE, ctypes.POINTER(Memory), w.DWORD]
        kernel.K32GetProcessMemoryInfo.restype = w.BOOL
        value = Memory(cb=ctypes.sizeof(Memory))
        assert kernel.K32GetProcessMemoryInfo(handle.handle, ctypes.byref(value), value.cb)
        return {"identity": identity, "workingSet": value.workingSet, "privateBytes": value.privateBytes,
                "peakWorkingSet": value.peakWorkingSet, "peakPrivateCommit": value.peakPagefile}

def selected(root, maximum=8):
    cohort = json.loads(COHORT.read_bytes())
    assert cohort["sourceTreeClean"]
    for file in cohort["files"]:
        source = PAYLOAD / file["path"]
        assert source.stat().st_size == file["size"] and digest(source) == file["sha256"]
        target = root / "backend" / file["path"]
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    result = subprocess.run([str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        str(ROOT / "scripts/New-McpFrontdoorPolicy.ps1"), "-Destination", str(root / "backend/frontdoor.json"),
        "-MaximumMetadataClients", str(maximum), "-MaximumStartingClients", "2"], env=dict(os.environ, PSModulePath=""),
        capture_output=True, timeout=20, creationflags=0x08000000)
    assert result.returncode == 0, result.stderr.decode(errors="replace")
    files = [{"path": "Mcp/" + file.relative_to(root / "backend").as_posix(), "size": file.stat().st_size, "sha256": digest(file)}
             for file in sorted((root / "backend").rglob("*")) if file.is_file()]
    candidate = root / "candidate.json"
    candidate.write_bytes(json.dumps({"version": cohort["version"], "sourceCommit": cohort["commit"], "sourceTreeClean": True,
        "kind": "MCP frontend closure only", "state": "staged-not-selected", "coreSha256": digest(root / "backend/Studio5000.Core.dll"),
        "payloads": files}).encode())
    state = root / "selected"
    publish = subprocess.run([str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        str(ROOT / "scripts/Publish-StableMcp.ps1"), "-SourceDirectory", str(root / "backend"),
        "-CandidateManifest", str(candidate), "-ExpectedManifestSha256", digest(candidate), "-AppRoot", str(root),
        "-StateRoot", str(state), "-ExpectedPointerSha256", "absent"], capture_output=True, timeout=30,
        env=dict(os.environ, PSModulePath=""), creationflags=0x08000000)
    (root / "selection.stdout.json").write_bytes(publish.stdout)
    (root / "selection.stderr.log").write_bytes(publish.stderr)
    assert publish.returncode == 0, publish.stderr.decode(errors="replace")
    return state

class Connection:
    def __init__(self, root, state, name):
        self.root = root
        self.stderr = (root / (name + ".stderr.log")).open("xb")
        self.child = subprocess.Popen([str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(state / "Start-Mcp.ps1")],
            cwd=state, env=dict(os.environ, PSModulePath="", ROCKWELL_RESOURCE_REGISTRY=str(root / "registry")),
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr, creationflags=0x08000000)
        self.messages = []
        self.replies = queue.Queue()
        self.identifier = 0
        self.name = name
        self.closed = False
        def reader():
            try:
                for line in self.child.stdout:
                    value = json.loads(line)
                    self.messages.append(value)
                    self.replies.put(value)
                self.replies.put(None)
            except BaseException as error:
                self.replies.put(error)
        self.thread = threading.Thread(target=reader, daemon=True)
        self.thread.start()
    def rpc(self, method, params=None, notification=False):
        self.identifier += 1
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        if not notification:
            message["id"] = self.identifier
        self.child.stdin.write((json.dumps(message) + "\n").encode())
        self.child.stdin.flush()
        if notification:
            return
        while True:
            reply = self.replies.get(timeout=55)
            assert reply is not None, "Unexpected selected-path EOF"
            if isinstance(reply, BaseException):
                raise reply
            if reply.get("id") == self.identifier:
                assert "error" not in reply, reply
                return reply["result"]
    def initialize(self):
        result = self.rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "isolated-frontdoor-fixture", "version": "1"}})
        self.rpc("notifications/initialized", notification=True)
        assert len(self.rpc("tools/list", {})["tools"]) == 59
        return result
    def call(self, name, arguments):
        result = self.rpc("tools/call", {"name": name, "arguments": arguments})
        assert not result.get("isError"), result
        return result.get("structuredContent") or json.loads(next(x["text"] for x in result["content"] if x["type"] == "text"))
    def close(self, require_zero=True):
        if self.closed:
            return
        if not self.child.stdin.closed:
            self.child.stdin.close()
        code = self.child.wait(timeout=15)
        self.thread.join(timeout=3)
        self.stderr.close()
        (self.root / (self.name + ".messages.json")).write_bytes(json.dumps(self.messages, indent=2).encode())
        self.closed = True
        if require_zero:
            assert code == 0
    def __enter__(self):
        return self
    def __exit__(self, *args):
        self.close()

def test_fifth_selected_frontdoor_discovers_and_reads_real_fixture_guidance_at_execution_saturation(fixture):
    ledger = ResourceLedger(fixture / "registry")
    native = ledger.owner("rockwell-mcp-server")
    for _ in range(16):
        native.begin_launch("Retained uncertain fixture", kind="sdk-worker")
    original = {p: p.read_bytes() for p in (fixture / "registry/resources").glob("*.json")}
    state = selected(fixture)
    with ExitStack() as held:
        slots = fixture / ".local/mcp-clients"
        slots.mkdir()
        for i in range(4):
            held.enter_context((slots / f"slot-{i}.lock").open("a+b"))
        with Connection(fixture, state, "fifth") as connection:
            initialized = connection.initialize()
            assert "bounded metadata frontdoor" in initialized["instructions"]
            capabilities = connection.call("studio_capabilities", {})
            assert capabilities["offlineFixture"]
            catalog = connection.call("rockwell_broker_capabilities", {})
            guide = catalog["capabilities"][0]
            document = connection.call("rockwell_read_workflow", guide["discovery"]["arguments"])
            assert hashlib.sha256(document["instructions"]["text"].encode()).hexdigest() == document["instructions"]["sha256"]
            rows = [r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor"]
            active = next(r for r in rows if r.state == "active")
            assert active.process is None and active.ownership == "attached"
            assert ledger.driver.state(active.owner.model_dump()) == "alive"
            (fixture / "active-frontdoor.json").write_bytes(json.dumps(active.model_dump(mode="json")).encode())
        retired = ledger.get(active.resource_id)
        assert retired.state == "released" and retired.reconciliation.actor == active.owner
        assert ledger.driver.state(active.owner.model_dump()) in {"absent", "different"}
        assert all(p.read_bytes() == content for p, content in original.items())
        service = json.loads((fixture / ".local/service-owner.json").read_bytes())
        assert ledger.driver.state(service["owner"]) == "alive"
    assert len((fixture / "fixture-service-starts.jsonl").read_text().splitlines()) == 1

def test_selected_frontdoor_crash_releases_os_slot_and_retains_claim_until_next_actual_owner(fixture):
    state = selected(fixture)
    connection = Connection(fixture, state, "crash")
    try:
        connection.initialize()
        ledger = ResourceLedger(fixture / "registry")
        old = next(r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor" and r.state == "active")
        # Exact fixture-owned generation only; never a name/global sweep.
        with ledger.driver.open(old.owner.model_dump()) as handle:
            handle.terminate()
            assert handle.wait(5000)
        connection.child.wait(timeout=15)
        assert ledger.get(old.resource_id).revision == old.revision
    finally:
        if not connection.child.stdin.closed:
            connection.child.stdin.close()
        connection.stderr.close()
        (fixture / "crash.messages.json").write_bytes(json.dumps(connection.messages).encode())
    with Connection(fixture, state, "replacement") as replacement:
        replacement.initialize()
        recovered = ledger.get(old.resource_id)
        assert recovered.state == "released" and recovered.owner == old.owner
        assert recovered.reconciliation.previous_revision == old.revision

def test_selected_parent_loss_stops_only_its_transport_and_preserves_the_genuine_fixture_service(fixture):
    state = selected(fixture)
    connection = Connection(fixture, state, "parent-loss")
    ledger = ResourceLedger(fixture / "registry")
    try:
        connection.initialize()
        connection.call("studio_capabilities", {})
        owner = next(r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor" and r.state == "active")
        service = json.loads((fixture / ".local/service-owner.json").read_bytes())
        parent = ledger.driver.capture_handle(int(connection.child._handle), connection.child.pid)
        (fixture / "parent-loss-identities.json").write_bytes(json.dumps({"launcher": parent, "frontdoor": owner.owner.model_dump(),
            "service": service["owner"], "stdinHeldOpenByFixture": True}).encode())
        with ledger.driver._open(owner.owner.pid) as handle:
            assert handle.identity == owner.owner.model_dump() and handle.alive()
            connection.child.terminate()  # Retained creation handle of this selected fixture launcher only.
            connection.child.wait(timeout=10)
            assert handle.wait(10000), "Selected SDK host survived its exact launcher loss"
        retired = ledger.get(owner.resource_id)
        assert retired.state == "released" and retired.reconciliation.actor == owner.owner
        assert ledger.driver.state(service["owner"]) == "alive"
    finally:
        connection.close(require_zero=False)

def test_normal_and_reconnect_overlap_keep_execution_claims_and_record_exact_process_memory(fixture):
    ledger = ResourceLedger(fixture / "registry")
    protected = ledger.owner("rockwell-mcp-server")
    for _ in range(16):
        protected.begin_launch("Unknown native fixture", kind="sdk-worker")
    original = {p: p.read_bytes() for p in (fixture / "registry/resources").glob("*.json")}
    state = selected(fixture, maximum=10)
    with ExitStack() as cleanup:
        slots = fixture / ".local/mcp-clients"
        slots.mkdir()
        for i in range(4):
            cleanup.enter_context((slots / f"slot-{i}.lock").open("a+b"))
        clients = [cleanup.enter_context(Connection(fixture, state, f"normal-{i}")) for i in range(3)]
        for connection in clients:
            connection.initialize()
        clients[0].call("studio_capabilities", {})
        normal = [r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor" and r.state == "active"]
        assert len(normal) == 3
        overlap = cleanup.enter_context(Connection(fixture, state, "reconnect-overlap"))
        overlap.initialize()
        current = [r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor" and r.state == "active"]
        assert len(current) == 4
        observations = [observe(ledger.driver, r.owner.model_dump()) for r in current]
        launchers = [observe(ledger.driver, ledger.driver.capture_handle(int(c.child._handle), c.child.pid)) for c in [*clients, overlap]]
        assert all(x["privateBytes"] < 128 * 1024 * 1024 for x in observations)
        assert sum(x["privateBytes"] for x in [*observations, *launchers]) < 4 * 256 * 1024 * 1024
        (fixture / "normal-transition-memory.json").write_bytes(json.dumps({"normalFrontdoors": 3, "transitionFrontdoors": 4,
            "knownTransportProcesses": 8, "frontdoors": observations, "launchers": launchers,
            "legacyHandleLeases": 4, "executionClaimsRetained": 16, "isFixture": True}, indent=2).encode())
        overlap.close()
        assert all(p.read_bytes() == value for p, value in original.items())
    assert all(r.state == "released" for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor")

def test_foreign_unknown_metadata_observation_is_retained_across_another_selected_connection(fixture):
    state = selected(fixture)
    ledger = ResourceLedger(fixture / "registry")
    with Connection(fixture, state, "template") as first:
        first.initialize()
        template = next(r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor").model_dump(mode="json")
    template["resource_id"] = uuid.uuid4().hex
    template["owner"]["host_id"] = "0" * 64
    template["owner_id"] = uuid.uuid4().hex
    template["state"] = "active"
    template["reconciliation"] = None
    path = fixture / "registry/resources" / (template["resource_id"] + ".json")
    original = json.dumps(template, separators=(",", ":")).encode()
    path.write_bytes(original)
    with Connection(fixture, state, "unknown-preserved") as later:
        later.initialize()
        assert path.read_bytes() == original
    assert path.read_bytes() == original

def test_metadata_reads_remain_available_when_forwarding_is_refused_before_dispatch(fixture):
    state = selected(fixture)
    with Connection(fixture, state, "forwarding-refused") as connection:
        connection.initialize()
        directory = fixture / ".local/mcp-frontdoor-calls"
        directory.mkdir()
        with ExitStack() as held:
            for i in range(4):
                held.enter_context((directory / f"slot-{i}.lock").open("a+b"))
            assert connection.call("studio_capabilities", {})["offlineFixture"]
            refused = connection.rpc("tools/call", {"name": "studio_list_projects", "arguments": {}})
            assert refused["isError"]
            detail = refused.get("structuredContent") or json.loads(refused["content"][0]["text"])
            assert detail["code"] == "mcp_forwarding_capacity" and detail["dispatched"] is False
            assert detail["automaticRetry"] is False

def test_metadata_limit_refusal_adds_no_unbound_claim_and_preserves_admitted_clients(fixture):
    state = selected(fixture, maximum=4)
    ledger = ResourceLedger(fixture / "registry")
    with ExitStack() as cleanup:
        clients = [cleanup.enter_context(Connection(fixture, state, f"held-{i}")) for i in range(4)]
        for client in clients:
            client.initialize()
        rows = [r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor"]
        assert len(rows) == 4
        refused = Connection(fixture, state, "limit-refused")
        try:
            assert refused.child.wait(timeout=20) != 0
            assert len([r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor"]) == 4
            assert all(ledger.driver.state(r.owner.model_dump()) == "alive" for r in rows)
        finally:
            refused.close(require_zero=False)
