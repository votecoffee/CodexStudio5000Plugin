"""Opt-in harmless service owner/race fixtures; no vendor or normal service mutation."""
import json
import os
import shutil
import socket
import subprocess
import threading
import queue
import time
from pathlib import Path
import pytest
from universal_tools.resources import ResourceLedger

ROOT = Path(__file__).resolve().parents[2]
HELPER_DIR = ROOT / "tests/Studio5000.ResourceInterop/bin/Release/net8.0"
HELPER = HELPER_DIR / "Studio5000.ResourceInterop.exe"
POWERSHELL = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
pytestmark = pytest.mark.skipif(os.name != "nt" or os.environ.get("ROCKWELL_DOTNET_RESOURCE_PROCESSES") != "1",
                               reason="Explicit opt-in for disposable offline owner fixtures")

@pytest.fixture
def fixture(tmp_path):
    assert HELPER.is_file()
    (tmp_path / ".resource-interop-fixture").write_text("Harmless offline service fixture")
    service = tmp_path / "artifacts/app/Service"
    service.mkdir(parents=True)
    for file in HELPER_DIR.iterdir():
        if file.is_file():
            shutil.copy2(file, service / file.name)
    shutil.copy2(HELPER, service / "Studio5000.Service.exe")
    shutil.copy2(HELPER_DIR / "Studio5000.ResourceInterop.dll", service / "Studio5000.Service.dll")
    worker = tmp_path / "artifacts/app/ExtensionWorker"
    worker.mkdir(parents=True)
    for original, name in [(HELPER, "Studio5000.ExtensionWorker.exe"),
                           (HELPER_DIR / "Studio5000.ResourceInterop.dll", "Studio5000.ExtensionWorker.dll"),
                           (HELPER_DIR / "Studio5000.Core.dll", "Studio5000.Core.dll")]:
        shutil.copy2(original, worker / name)
    with socket.socket() as available:
        available.bind(("127.0.0.1", 0))
        port = available.getsockname()[1]
    settings = {"ServiceUrl": f"http://127.0.0.1:{port}", "Studio5000": {
        "EnableControllerOperations": False, "EnableSdkServiceRecovery": False, "AutoStartSdkService": False,
        "NativeBridgePath": None, "CipBridgePath": None, "ExtensionWorkerPath": "artifacts/app/ExtensionWorker/Studio5000.ExtensionWorker.exe"}}
    settings["Studio5000"].update(MaximumOwnedResources=16, MaximumOwnedProcesses=8, MaximumMcpClients=4, McpShutdownTimeoutSeconds=5)
    (tmp_path / "service.local.json").write_text(json.dumps(settings), encoding="utf-8")
    (tmp_path / "scripts").mkdir()
    (tmp_path / ".local").mkdir()
    (tmp_path / ".local/empty.stdin").write_text("")
    (tmp_path / "scripts/Start-Service.ps1").write_text(r'''
param([switch]$Background)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if (!(Test-Path -LiteralPath (Join-Path $root '.resource-interop-fixture'))) { throw 'Fixture marker missing' }
[IO.File]::AppendAllText((Join-Path $root 'fixture-starter-attempts.txt'),('attempt'+[Environment]::NewLine))
if (Test-Path -LiteralPath (Join-Path $root 'fixture-fail-start')) { throw 'Explicit harmless startup refusal' }
if (Test-Path -LiteralPath (Join-Path $root 'fixture-delay-start')) {
    $fixtureDeadline=[DateTime]::UtcNow.AddSeconds(25)
    while (!(Test-Path -LiteralPath (Join-Path $root 'fixture-release-start'))) {
        if ([DateTime]::UtcNow -gt $fixtureDeadline) { throw 'Fixture release deadline expired' }
        Start-Sleep -Milliseconds 50
    }
}
$arguments=@('bootstrap-service',('"' + $root + '"'))
$process=Start-Process -FilePath (Join-Path $root 'artifacts/app/Service/Studio5000.Service.exe') -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardInput (Join-Path $root '.local/empty.stdin') -RedirectStandardOutput (Join-Path $root '.local/fixture-service.stdout.log') -RedirectStandardError (Join-Path $root '.local/fixture-service.stderr.log')
$process.Id
''', encoding="utf-8")
    (tmp_path / "backend").mkdir()
    version = json.loads((ROOT / "release-version.json").read_bytes())["displayVersion"]
    generated = subprocess.run([str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                    str(ROOT / "scripts/New-OfflineServicePin.ps1"), "-AppRoot", str(tmp_path),
                    "-Destination", str(tmp_path / "backend/offline-service.json"), "-ExpectedServiceVersion", version],
                   env=dict(os.environ, PSModulePath=""), capture_output=True, timeout=30, creationflags=0x08000000)
    (tmp_path / "pin-generator.stdout.log").write_bytes(generated.stdout)
    (tmp_path / "pin-generator.stderr.log").write_bytes(generated.stderr)
    assert generated.returncode == 0, generated.stderr.decode(errors="replace")
    yield tmp_path
    receipt = tmp_path / ".local/service-owner.json"
    if receipt.exists():
        owner = json.loads(receipt.read_bytes())["owner"]
        driver = ResourceLedger(tmp_path / "registry").driver
        (tmp_path / "fixture-stop").write_text("Stop only this disposable fixture")
        deadline = time.monotonic() + 10
        while driver.state(owner) not in {"absent", "different"}:
            if time.monotonic() > deadline:
                raise AssertionError("Disposable service did not cooperatively stop; retained for inspection")
            time.sleep(.05)

def start(root):
    return subprocess.Popen([str(HELPER), "bootstrap-ensure", str(root)], stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=0x08000000)

def wait(child):
    stdout, stderr = child.communicate(timeout=55)
    assert child.returncode == 0, (stdout.decode(errors="replace"), stderr.decode(errors="replace"))

def test_competing_actual_owners_start_one_genuine_service_that_survives_frontend_exit(fixture):
    before = (fixture / "service.local.json").read_bytes()
    children = [start(fixture) for _ in range(4)]
    for child in children:
        wait(child)
    results = [json.loads(p.read_bytes()) for p in fixture.glob("bootstrap-result-*.json")]
    assert len(results) == 4 and sum(x["result"]["started"] for x in results) == 1
    receipt = json.loads((fixture / ".local/service-owner.json").read_bytes())
    driver = ResourceLedger(fixture / "registry").driver
    assert driver.state(receipt["owner"]) == "alive"
    assert len((fixture / "fixture-service-starts.jsonl").read_text().splitlines()) == 1
    assert all(x["actor"] != receipt["owner"] for x in results)
    assert (fixture / "service.local.json").read_bytes() == before

def test_observed_service_owner_loss_starts_a_distinct_generation_without_replaying_jobs(fixture):
    wait(start(fixture))
    old = json.loads((fixture / ".local/service-owner.json").read_bytes())["owner"]
    driver = ResourceLedger(fixture / "registry").driver
    assert old["executable"] == str(fixture / "artifacts/app/Service/Studio5000.Service.exe").lower()
    with driver.open(old) as handle:
        handle.terminate()
        assert handle.wait(5000)
    wait(start(fixture))
    new = json.loads((fixture / ".local/service-owner.json").read_bytes())["owner"]
    assert new != old and driver.state(new) == "alive"
    assert len((fixture / "fixture-service-starts.jsonl").read_text().splitlines()) == 2
    assert not list(fixture.glob("**/*sdk*"))

def test_failed_starter_retains_uncertainty_and_later_request_does_not_dispatch_again(fixture):
    (fixture / "fixture-fail-start").write_text("Explicit refusal before fake service launch")
    for _ in range(2):
        child = start(fixture)
        child.communicate(timeout=55)
        assert child.returncode != 0
    journal = json.loads((fixture / ".local/offline-service-start.json").read_bytes())
    assert journal["phase"] == "uncertain"
    assert len((fixture / "fixture-starter-attempts.txt").read_text().splitlines()) == 1
    assert not (fixture / ".local/service-owner.json").exists()

def test_actual_mcp_discovers_before_cold_service_and_gates_request_until_ready(fixture):
    mcp = ROOT / "artifacts/test-tools/bootstrap-mcp"
    assert (mcp / "Studio5000.StdioHost.exe").is_file()
    for file in mcp.iterdir():
        if file.is_file() and file.name not in {"offline-service.json", "backend.json"}:
            shutil.copy2(file, fixture / "backend" / file.name)
    (fixture / "fixture-delay-start").write_text("Known harmless startup delay")
    env = dict(os.environ, STUDIO5000_PLUGIN_ROOT=str(fixture), ROCKWELL_RESOURCE_REGISTRY=str(fixture / "registry"))
    stderr = (fixture / "mcp.stderr.log").open("xb")
    child = subprocess.Popen([str(fixture / "backend/Studio5000.StdioHost.exe")], cwd=fixture, env=env,
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr, creationflags=0x08000000)
    replies = queue.Queue()
    def reader():
        for line in child.stdout:
            replies.put(json.loads(line))
        replies.put(None)
    threading.Thread(target=reader, daemon=True).start()
    messages = []
    def rpc(identifier, method, params):
        request = {"jsonrpc": "2.0", "method": method, "params": params}
        if identifier is not None:
            request["id"] = identifier
        child.stdin.write((json.dumps(request) + "\n").encode())
        child.stdin.flush()
        if identifier is None:
            return
        while True:
            reply = replies.get(timeout=40)
            assert reply is not None
            messages.append(reply)
            if reply.get("id") == identifier:
                assert "error" not in reply, reply
                return reply["result"]
    try:
        rpc(1, "initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "offline-cold-start-fixture", "version": "1"}})
        rpc(None, "notifications/initialized", {})
        assert not (fixture / ".local/service-owner.json").exists()
        assert len(rpc(2, "tools/list", {})["tools"]) == 59
        (fixture / "fixture-release-start").write_text("Metadata discovery finished; allow only the harmless service")
        result = rpc(3, "tools/call", {"name": "studio_capabilities", "arguments": {}})
        assert not result.get("isError"), result
        assert (fixture / "fixture-api-dispatches.txt").read_text().splitlines() == ["capabilities"]
        child.stdin.close()
        assert child.wait(timeout=10) == 0
        ledger = ResourceLedger(fixture / "registry")
        record = next(r for r in ledger.records() if r.kind == "mcp-adapter")
        assert record.state == "terminated" and record.receipt.outcome == "terminated"
        owner = json.loads((fixture / ".local/service-owner.json").read_bytes())["owner"]
        assert ledger.driver.state(owner) == "alive"
    finally:
        if child.poll() is None:
            child.stdin.close()
            child.wait(timeout=10)
        stderr.close()
        (fixture / "mcp.messages.json").write_text(json.dumps(messages, indent=2), encoding="utf-8")

def test_actual_full_frontend_refusal_never_creates_another_unknown_reservation(fixture):
    mcp = ROOT / "artifacts/test-tools/bootstrap-mcp"
    for file in mcp.iterdir():
        if file.is_file() and file.name not in {"offline-service.json", "backend.json"}:
            shutil.copy2(file, fixture / "backend" / file.name)
    ledger = ResourceLedger(fixture / "registry", max_active_per_app=16)
    owner = ledger.owner("rockwell-mcp-server")
    for _ in range(16):
        owner.begin_launch("Preserved synthetic uncertainty", kind="mcp-adapter", cleanup_mode="terminate")
    before = {p.name: p.read_bytes() for p in ledger.directory.glob("*.json")}
    env = dict(os.environ, STUDIO5000_PLUGIN_ROOT=str(fixture), ROCKWELL_RESOURCE_REGISTRY=str(fixture / "registry"))
    for attempt in range(3):
        result = subprocess.run([str(fixture / "backend/Studio5000.StdioHost.exe")], cwd=fixture, env=env,
                                stdin=subprocess.DEVNULL, capture_output=True, timeout=20, creationflags=0x08000000)
        (fixture / f"refusal-{attempt}.stderr.log").write_bytes(result.stderr)
        assert result.returncode != 0 and b"no stateless reservation was created" in result.stderr
    assert before == {p.name: p.read_bytes() for p in ledger.directory.glob("*.json")}
    assert not (fixture / "fixture-service-starts.jsonl").exists()
