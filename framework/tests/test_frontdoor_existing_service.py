"""Opt-in existing-service-only selected fixtures; never use an installed service."""
from contextlib import contextmanager, ExitStack
import hashlib
import json
import os
import subprocess
import time
import urllib.request

import pytest
from test_frontdoor import Connection, PAYLOAD, observe, selected
from test_offline_bootstrap import fixture  # noqa: F401 - pytest fixture registration
from universal_tools.resources import ResourceLedger

pytestmark = pytest.mark.skipif(os.name != "nt" or os.environ.get("ROCKWELL_DOTNET_RESOURCE_PROCESSES") != "1",
                               reason="Explicit opt-in for isolated selected-path process fixtures")


def snapshots(root):
    return {p: p.read_bytes() for p in (root / ".local").rglob("*") if p.is_file() and
            (p.name.startswith("offline-service-start") or p.name in {"service-owner.json", "connection.json", "service.key"})}


def prepare(root):
    assert json.loads((PAYLOAD / "frontdoor.json").read_bytes())["serviceMode"] == "existing-genuine-only"
    (root / "fixture-fail-start").write_text("Starting the fixture service through bootstrap is forbidden in this profile")
    return selected(root)


def no_startup(root):
    assert not (root / "fixture-starter-attempts.txt").exists()
    assert not (root / ".local/offline-service-start.json").exists()
    assert not (root / ".local/offline-service-start.lock").exists()


def refused(connection):
    result = connection.rpc("tools/call", {"name": "studio_capabilities", "arguments": {}})
    assert result["isError"]
    detail = result.get("structuredContent") or json.loads(result["content"][0]["text"])
    assert detail["code"] == "mcp_existing_service_unavailable"
    assert detail["serviceMode"] == "existing-genuine-only"
    assert detail["dispatched"] is False and detail["automaticRetry"] is False
    return detail


@contextmanager
def genuine_fixture_service(root):
    # Explicit fixture preparation independent of the selected frontend. The
    # exact creation handle is retained. No scripts/Start-Service or SDK runs.
    with (root / "existing-service.stdout.log").open("xb") as stdout, (root / "existing-service.stderr.log").open("xb") as stderr:
        child = subprocess.Popen([str(root / "artifacts/app/Service/Studio5000.Service.exe"), "bootstrap-service", str(root)],
            cwd=root, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr,
            env=dict(os.environ, ROCKWELL_RESOURCE_REGISTRY=str(root / "registry")), creationflags=0x08000000)
        try:
            deadline = time.monotonic() + 15
            url = json.loads((root / "service.local.json").read_bytes())["ServiceUrl"] + "/health"
            while True:
                assert child.poll() is None, "Explicit harmless fixture service exited before readiness"
                try:
                    with urllib.request.urlopen(url, timeout=1) as response:
                        if response.status == 200:
                            break
                except OSError:
                    pass
                assert time.monotonic() < deadline, "Explicit harmless fixture service did not become ready"
                time.sleep(.05)
            receipt = json.loads((root / ".local/service-owner.json").read_bytes())
            driver = ResourceLedger(root / "registry").driver
            assert driver.capture_handle(int(child._handle), child.pid) == receipt["owner"]
            yield child, receipt
        finally:
            if child.poll() is None:
                (root / "fixture-stop").write_text("Cooperatively close only this disposable service")
                assert child.wait(timeout=10) == 0


def test_existing_only_missing_service_keeps_discovery_but_refuses_without_startup(fixture):
    state = prepare(fixture)
    with Connection(fixture, state, "missing-service") as connection:
        initialized = connection.initialize()
        assert "existing-genuine-only" in initialized["instructions"]
        refused(connection)
        refused(connection)
        no_startup(fixture)
    rows = ResourceLedger(fixture / "registry").records()
    assert len(rows) == 1 and rows[0].state == "released" and rows[0].reconciliation is not None
    assert rows[0].process is None and rows[0].receipt is None
    no_startup(fixture)


def test_existing_only_guidance_refusal_memory_and_eof_preserve_preexisting_service(fixture):
    ledger = ResourceLedger(fixture / "registry")
    for _ in range(16):
        ledger.owner("rockwell-mcp-server").begin_launch("Protected uncertain fixture", kind="sdk-worker")
    protected = {p: p.read_bytes() for p in (fixture / "registry/resources").glob("*.json")}
    with genuine_fixture_service(fixture) as (service, receipt):
        baseline = snapshots(fixture)
        state = prepare(fixture)
        with Connection(fixture, state, "existing-service") as connection:
            connection.initialize()
            assert connection.call("studio_capabilities", {})["offlineFixture"]
            catalog = connection.call("rockwell_broker_capabilities", {})
            guide = catalog["capabilities"][0]
            document = connection.call("rockwell_read_workflow", guide["discovery"]["arguments"])
            assert hashlib.sha256(document["instructions"]["text"].encode()).hexdigest() == document["instructions"]["sha256"]
            active = next(r for r in ledger.records() if r.app_id == "rockwell-mcp-frontdoor" and r.state == "active")
            memory = {"sdk": observe(ledger.driver, active.owner.model_dump()),
                      "launcher": observe(ledger.driver, ledger.driver.capture_handle(int(connection.child._handle), connection.child.pid)),
                      "service": receipt["owner"], "isFixture": True, "legacyLoadBaselineNotRepeated": True}
            assert memory["sdk"]["privateBytes"] < 128 * 1024 * 1024
            assert memory["sdk"]["privateBytes"] + memory["launcher"]["privateBytes"] < 256 * 1024 * 1024
            (fixture / "existing-service-memory.json").write_bytes(json.dumps(memory, indent=2).encode())
            slots = fixture / ".local/mcp-frontdoor-calls"
            slots.mkdir()
            with ExitStack() as held:
                for i in range(4):
                    held.enter_context((slots / f"slot-{i}.lock").open("a+b"))
                result = connection.rpc("tools/call", {"name": "studio_list_projects", "arguments": {}})
                detail = result.get("structuredContent") or json.loads(result["content"][0]["text"])
                assert result["isError"] and detail["code"] == "mcp_forwarding_capacity"
                assert detail["dispatched"] is False and detail["automaticRetry"] is False
                assert connection.call("studio_capabilities", {})["offlineFixture"]
        terminal = ledger.get(active.resource_id)
        assert terminal.state == "released" and terminal.reconciliation.actor == active.owner and terminal.receipt is None
        assert ledger.driver.state(receipt["owner"]) == "alive" and service.poll() is None
        assert snapshots(fixture) == baseline
        assert all(p.read_bytes() == value for p, value in protected.items())
        no_startup(fixture)


def test_existing_only_owner_loss_refuses_without_replacement_or_intent(fixture):
    with genuine_fixture_service(fixture) as (service, receipt):
        state = prepare(fixture)
        with Connection(fixture, state, "owner-loss") as connection:
            connection.initialize()
            assert connection.call("studio_capabilities", {})["offlineFixture"]
            baseline = snapshots(fixture)
            # Only this test's retained creation handle, after matching its full
            # self-published identity. Never act on an installed/name-only PID.
            service.terminate()
            service.wait(timeout=10)
            assert ResourceLedger(fixture / "registry").driver.state(receipt["owner"]) in {"absent", "different"}
            refused(connection)
            refused(connection)
            assert snapshots(fixture) == baseline
            no_startup(fixture)
    no_startup(fixture)
