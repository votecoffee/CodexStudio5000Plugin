"""Optional exact legacy-image proof in copied, isolated state only."""
import hashlib
import ctypes
from ctypes import wintypes
from contextlib import contextmanager
import json
import os
import shutil
import subprocess
import time
from pathlib import Path
import pytest
from universal_tools.resources import ResourceLedger
from universal_tools.models import BrokerError

from test_offline_bootstrap import fixture, HELPER

LEGACY = os.environ.get("ROCKWELL_LEGACY_FRONTEND_DIRECTORY")
pytestmark = pytest.mark.skipif(os.name != "nt" or os.environ.get("ROCKWELL_DOTNET_RESOURCE_PROCESSES") != "1" or not LEGACY,
                               reason="Explicit opt-in and reviewed original legacy closure required")
SOURCE = "31a57922bde082f82567ebcc035b29ab0a35d9da"
def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

@contextmanager
def fixture_lock(ledger):
    # Compete only for acquisition; do not replay the crash or retirement.
    deadline = time.monotonic() + 3
    while True:
        lock = ledger._locked()
        try:
            lock.__enter__()
            break
        except BrokerError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(.001)
    try:
        yield
    finally:
        lock.__exit__(None, None, None)

def child_pids(parent):
    """Snapshot only, then pin each observed child through its process handle."""
    class Entry(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD), ("pid", wintypes.DWORD),
                    ("heap", ctypes.c_size_t), ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
                    ("parent", wintypes.DWORD), ("priority", wintypes.LONG), ("flags", wintypes.DWORD),
                    ("image", wintypes.WCHAR * 260)]
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    assert snapshot != ctypes.c_void_p(-1).value
    try:
        entry = Entry(size=ctypes.sizeof(Entry))
        present = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
        while present:
            if entry.parent == parent:
                yield entry.pid
            present = kernel.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        kernel.CloseHandle(snapshot)


def test_exact_legacy_unbound_owner_crash_has_reviewed_stateless_retirement_evidence(fixture):
    original = Path(LEGACY)
    manifest = json.loads((original / "backend.json").read_bytes())
    assert manifest["source_commit"] == SOURCE
    copied = fixture / "legacy"
    copied.mkdir()
    for entry in manifest["files"]:
        source = original / entry["path"]
        assert digest(source) == entry["sha256"]
        target = copied / entry["path"]
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    shutil.copy2(original / "backend.json", copied / "backend.json")
    captured = None
    for attempt in range(4):
        registry = fixture / f"legacy-registry-{attempt}"
        ledger = ResourceLedger(registry)
        env = dict(os.environ, STUDIO5000_PLUGIN_ROOT=str(fixture), ROCKWELL_RESOURCE_REGISTRY=str(registry))
        with (fixture / f"legacy-{attempt}.stderr.log").open("xb") as stderr:
            child = subprocess.Popen([str(copied / "Studio5000.StdioHost.exe")], cwd=fixture, env=env,
                                     stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=stderr, creationflags=0x08000000)
            # Popen retains the actual creation handle. Never replace departure
            # evidence with a PID-only observation or a generated stub owner.
            owner_identity = ledger.driver.capture_handle(int(child._handle), child.pid)
            deadline = time.monotonic() + 10
            while child.poll() is None and not ledger.records():
                if time.monotonic() > deadline:
                    raise AssertionError("Copied legacy owner did not publish its intent")
                time.sleep(.001)
            if ledger.records():
                # This lock gates the disposable bind window; it is NOT a
                # bind failure and we do not wait for voluntary exit while
                # blocking it. Crash only the exact Popen owner, then observe
                # the inherited private job close its still-unpublished child.
                with fixture_lock(ledger):
                    rows = ledger.records()
                    if len(rows) == 1 and rows[0].state == "launching" and rows[0].process is None:
                        assert rows[0].owner.model_dump(mode="json") == owner_identity
                        before = (registry / "resources" / (rows[0].resource_id + ".json")).read_bytes()
                        deadline = time.monotonic() + 5
                        adapter = None
                        while child.poll() is None and adapter is None:
                            for pid in child_pids(child.pid):
                                try:
                                    identity = ledger.driver.capture(pid)
                                    if identity["executable"] == str(copied / "Studio5000.Mcp.exe").lower():
                                        adapter = ledger.driver.open(identity)
                                        break
                                except (OSError, ProcessLookupError):
                                    pass
                            if time.monotonic() >= deadline:
                                raise AssertionError("No original unpublished adapter observed; fixture evidence is inconclusive")
                            time.sleep(.005)
                        assert adapter is not None and child.poll() is None
                        with adapter:
                            assert adapter.alive()
                            assert (registry / "resources" / (rows[0].resource_id + ".json")).read_bytes() == before
                            child.terminate()  # This test's retained creation handle, no PID sweep.
                            assert child.wait(timeout=10) != 0
                            assert adapter.wait(5000), "Original private job did not close its unpublished child"
                            evidence = {"scenario": "exact-original-owner-crash-during-unbound-bind-window",
                                        "bindFailureInferred": False, "owner": owner_identity,
                                        "adapter": adapter.identity, "ownerExit": child.returncode,
                                        "adapterHandleSignaled": True, "originalRevision": rows[0].revision,
                                        "legacyManifestSha256": digest(copied / "backend.json")}
                            (fixture / f"legacy-{attempt}.crash-evidence.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
                        assert (registry / "resources" / (rows[0].resource_id + ".json")).read_bytes() == before
            if child.poll() is None:
                child.stdin.close()  # Missed timing window: ordinary EOF, never assume crash.
                child.wait(timeout=15)
            else:
                child.stdin.close()
        rows = ledger.records()
        (fixture / f"legacy-{attempt}.records.json").write_text(json.dumps([r.model_dump(mode="json") for r in rows], indent=2), encoding="utf-8")
        if (fixture / f"legacy-{attempt}.crash-evidence.json").is_file() and len(rows) == 1 and rows[0].state == "launching" and rows[0].process is None:
            captured = (registry, rows[0])
            break
    assert captured is not None, "Legacy timing window not captured; all inconclusive evidence retained"
    registry, old = captured
    assert ResourceLedger(registry).driver.state(old.owner.model_dump()) in {"absent", "different"}
    # Keep the actual legacy record bytes and original creation identity.
    (fixture / "registry/resources").mkdir(parents=True, exist_ok=True)
    shutil.copy2(registry / "resources" / (old.resource_id + ".json"),
                 fixture / "registry/resources" / (old.resource_id + ".json"))
    pin_path = fixture / "backend/offline-service.json"
    pin = json.loads(pin_path.read_bytes())
    pin["previousFrontends"] = [{"directory": str(copied), "files": manifest["files"],
        "unboundLaunchContract": "windows-private-job-bind-before-resume-v1", "sourceCommit": SOURCE,
        "backendManifestSha256": digest(copied / "backend.json")}]
    pin_path.write_text(json.dumps(pin), encoding="utf-8")
    result = subprocess.run([str(HELPER), "bootstrap-prepare", str(fixture)], capture_output=True, timeout=20, creationflags=0x08000000)
    (fixture / "retirement.stderr.log").write_bytes(result.stderr)
    assert result.returncode == 0, result.stderr.decode(errors="replace")
    retired = ResourceLedger(fixture / "registry").get(old.resource_id)
    assert retired.state == "released" and retired.process is None
    assert retired.owner == old.owner and retired.reconciliation.previous_revision == old.revision
    proof = json.loads((fixture / "bootstrap-prepared.json").read_bytes())
    assert retired.reconciliation.actor.model_dump(mode="json") == proof["actor"]
    assert not (fixture / "fixture-service-starts.jsonl").exists()
