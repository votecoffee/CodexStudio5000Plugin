"""Harmless Python children of the .NET resource helper; no engineering hosts."""

import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest
from universal_tools.resources import ResourceLedger

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "tests/Studio5000.ResourceInterop/bin/Release/net8.0/Studio5000.ResourceInterop.dll"
pytestmark = pytest.mark.skipif(
    os.name != "nt" or os.environ.get("ROCKWELL_DOTNET_RESOURCE_PROCESSES") != "1",
    reason="Explicit opt-in required for harmless .NET/Python process fixtures",
)


@pytest.fixture
def fixture(tmp_path):
    assert HELPER.is_file()
    (tmp_path / ".resource-interop-fixture").write_text("Harmless process fixture")
    (tmp_path / "python-executable.txt").write_text(sys._base_executable, encoding="utf-8")
    return tmp_path


def run(mode, root):
    subprocess.run(
        ["dotnet", str(HELPER), mode, str(root)], check=True, capture_output=True, timeout=25
    )
    return ResourceLedger(root / "native-launch-registry").records()[0]


def test_redirected_binary_bytes_and_eof_are_preserved(fixture):
    payload = bytes(range(256)) * 256 + b'\n{"jsonrpc":"2.0","fixture":"\\u00fc"}\n'
    (fixture / "input.bin").write_bytes(payload)
    record = run("launch-echo", fixture)
    assert (fixture / "output.bin").read_bytes() == payload
    assert record.state == "terminated" and record.receipt.outcome == "terminated"


def test_descendants_are_recorded_and_close_with_the_exact_owned_group(fixture):
    record = run("launch-descendants", fixture)
    observed = json.loads((fixture / "launch-active.json").read_text("utf-8"))
    descendant_pid = int((fixture / "executed").read_text())
    assert {observed["process"]["pid"], descendant_pid}.issubset(
        p["pid"] for p in observed["members"]
    )
    assert record.state == "terminated"
    driver = ResourceLedger(fixture / "native-launch-registry").driver
    assert all(driver.state(p) in {"absent", "different"} for p in observed["members"])


def test_refusal_keeps_owned_child_alive_until_explicit_harmless_evidence(fixture):
    record = run("launch-refuse", fixture)
    assert record.state == "terminated" and record.barrier_resolution is not None
    assert record.barrier_resolution.evidence.startswith("Only the harmless fixture")


def test_failure_before_resume_never_executes_the_child(fixture):
    record = run("launch-fail", fixture)
    assert record.state == "failed" and record.receipt is None
    assert not (fixture / "executed").exists()


def test_crashed_native_owner_stops_nested_tree_and_preserves_unclaimed_record(fixture):
    harness = ResourceLedger(fixture / "harness-registry")
    with (
        harness.owner("rockwell-native-harness") as owner,
        owner.launch(
            [
                str(Path(os.environ["ProgramFiles"]) / "dotnet/dotnet.exe"),
                str(HELPER),
                "launch-hold",
                str(fixture),
            ],
            "Harmless nested launcher harness",
            allow_termination=True,
            max_processes=16,
        ) as launch,
    ):
        marker = fixture / "launch-active.json"
        deadline = time.monotonic() + 10
        while not marker.exists():
            if time.monotonic() >= deadline:
                raise AssertionError("Harmless native launcher did not become ready")
            time.sleep(0.01)
        observed = json.loads(marker.read_text("utf-8"))
        identity = harness.get(launch.resource_id).process.model_dump()
        with harness.driver.open(identity) as handle:
            handle.terminate()
            assert handle.wait(5000)
        ledger = ResourceLedger(fixture / "native-launch-registry")
        record = ledger.records()[0]
        assert record.state == "active" and record.receipt is None
        deadline = time.monotonic() + 5
        while any(ledger.driver.state(p) == "alive" for p in observed["members"]):
            if time.monotonic() >= deadline:
                raise AssertionError("Crashed owner left a harmless contained child alive")
            time.sleep(0.01)
        assert all(ledger.driver.state(p) in {"absent", "different"} for p in observed["members"])
