"""Opt-in JSON/lock interop helper only; no MCP, app host or native SDK."""

import json
import os
import subprocess
import time
from pathlib import Path

import pytest
from universal_tools.models import BrokerError
from universal_tools.resources import ResourceLedger, ResourceRecord

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "tests/Studio5000.ResourceInterop/bin/Release/net8.0/Studio5000.ResourceInterop.dll"
pytestmark = pytest.mark.skipif(
    os.name != "nt" or os.environ.get("ROCKWELL_DOTNET_RESOURCE_INTEROP") != "1",
    reason="Explicit opt-in required for harmless compiled JSON/lock helper",
)


def invoke(command, root, *, check=False):
    return subprocess.run(
        ["dotnet", str(HELPER), command, str(root)],
        check=check,
        capture_output=True,
        text=True,
        timeout=25,
    )


@pytest.fixture
def fixture(tmp_path):
    assert HELPER.is_file(), "Build the harmless ResourceInterop test helper first"
    root = tmp_path / "data-only-contract"
    result = invoke("fixtures", root, check=True)
    return root, json.loads(result.stdout)


def test_native_model_revisions_match_reviewed_python_runtime(fixture):
    root, summary = fixture
    count = 0
    with (root / "python.jsonl").open("w", encoding="utf-8") as output:
        for line in (root / "native.jsonl").read_text("utf-8").splitlines():
            item = json.loads(line)
            record = ResourceRecord.model_validate(item["record"])
            assert item["revision"] == record.revision, item["record"]["created_at"]
            output.write(record.model_dump_json() + "\n")
            count += 1
    assert count == summary["fixtures"] and count >= 1000
    invoke("roundtrip", root, check=True)
    for line in (root / "roundtrip.jsonl").read_text("utf-8").splitlines():
        item = json.loads(line)
        assert item["revision"] == ResourceRecord.model_validate(item["record"]).revision


def test_python_can_read_and_mutate_native_records_without_changing_actor(fixture):
    root, _ = fixture
    ledger = ResourceLedger(root / "registry")
    record = ledger.records()[0]

    class SyntheticDriver:
        def capture(self, pid):
            return record.owner.model_dump()

    ledger = ResourceLedger(root / "registry", driver=SyntheticDriver())
    owner = ledger.owner(record.app_id, owner_id=record.owner_id)
    changed = owner.set_recovery_barrier(record.resource_id, "Synthetic cross-runtime barrier")
    cleared = owner.resolve_recovery_barrier(
        record.resource_id, changed.revision, "Generated fixture has no native work"
    )
    assert cleared.barrier_resolution is not None
    with (root / "python.jsonl").open("w", encoding="utf-8") as output:
        output.write(cleared.model_dump_json() + "\n")
    invoke("roundtrip", root, check=True)
    native = json.loads((root / "roundtrip.jsonl").read_text("utf-8"))
    assert native["revision"] == cleared.revision
    assert owner.release_attachment(record.resource_id).state == "released"


def test_python_and_native_writers_use_the_same_os_byte_lock(fixture):
    root, _ = fixture
    ledger = ResourceLedger(root / "registry")
    with ledger._locked():
        result = invoke("admit", root)
        assert result.returncode == 23
    invoke("admit", root, check=True)
    process = subprocess.Popen(
        ["dotnet", str(HELPER), "lock", str(root)],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        deadline = time.monotonic() + 10
        while not (root / "lock-ready").exists():
            assert process.poll() is None
            if time.monotonic() >= deadline:
                raise AssertionError("Harmless lock helper did not become ready")
            time.sleep(0.01)
        with pytest.raises(BrokerError, match="already owns"), ledger._locked():
            pytest.fail("Python entered a native-owned lock")
    finally:
        (root / "lock-release").write_text("release", encoding="utf-8")
        try:
            process.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate(timeout=5)
    assert process.returncode == 0


def test_native_admission_waits_for_a_transient_foreign_writer_and_dispatches_once(fixture):
    root, _ = fixture
    ledger = ResourceLedger(root / "registry")
    before = {r.resource_id for r in ledger.records()}
    with ledger._locked():
        child = subprocess.Popen(["dotnet", str(HELPER), "admit", str(root)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        time.sleep(.35)
        assert child.poll() is None
    stdout, stderr = child.communicate(timeout=15)
    assert child.returncode == 0, stderr.decode(errors="replace")
    added = [r for r in ledger.records() if r.resource_id not in before]
    assert len(added) == 1 and stdout.decode().strip() == added[0].resource_id


def test_actual_harmless_identity_and_default_registry_match_python(fixture):
    from universal_tools.management import default_registry
    from universal_tools.windows_resources import ProcessDriver

    root, _ = fixture
    process = subprocess.Popen(
        ["dotnet", str(HELPER), "identity", str(root)],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        deadline = time.monotonic() + 10
        while not (root / "identity.json").exists():
            assert process.poll() is None
            if time.monotonic() >= deadline:
                raise AssertionError("Harmless identity helper did not become ready")
            time.sleep(0.01)
        data = json.loads((root / "identity.json").read_text("utf-8"))
        assert ProcessDriver().capture(process.pid) == data["identity"]
        assert str(default_registry()) == data["registry"]
    finally:
        (root / "identity-release").write_text("release", encoding="utf-8")
        try:
            process.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate(timeout=5)
    assert process.returncode == 0
