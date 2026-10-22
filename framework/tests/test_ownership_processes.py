"""Opt-in harmless Python trees only. Never an MCP, Rockwell or native-app host."""

import json
import os
import sys
import time

import pytest
from universal_tools.resources import ResourceLedger

from rockwell_framework.ownership import ServiceTree
from rockwell_framework.publication import APP_ID

pytestmark = pytest.mark.skipif(
    os.name != "nt" or os.environ.get("ROCKWELL_HARMLESS_RESOURCE_TESTS") != "1",
    reason="Explicit opt-in required for isolated harmless Windows process fixtures",
)


def await_file(path):
    deadline = time.monotonic() + 10
    while not path.exists():
        if time.monotonic() >= deadline:
            raise AssertionError("Harmless fixture did not publish its marker")
        time.sleep(0.02)
    return path.read_text("utf-8")


def test_harmless_tree_members_are_durable_and_normal_close_stops_descendants(tmp_path):
    ledger = ResourceLedger(tmp_path / "registry")
    marker = tmp_path / "descendant.pid"
    code = (
        "import subprocess,sys,time;from pathlib import Path;"
        "p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)']);"
        "Path(sys.argv[1]).write_text(str(p.pid),encoding='utf-8');time.sleep(60)"
    )
    with (
        ledger.owner(APP_ID) as owner,
        owner.launch(
            [sys.executable, "-c", code, str(marker)],
            "Harmless Python tree fixture",
            allow_termination=True,
            max_processes=8,
        ) as launch,
    ):
        descendant_pid = int(await_file(marker))
        record = launch.refresh()
        # Windows venv redirectors and console hosts can add intermediate
        # processes. Retain and verify closure of every observed member too.
        assert {launch.pid, descendant_pid}.issubset(p.pid for p in record.members)
        with ledger.driver.open(record.process.model_dump()) as handle:
            view = ServiceTree(ledger, launch, handle)
            assert view.poll() is None
            assert view.receipt_metadata()["pid"] == os.getpid()
    retained = ResourceLedger(tmp_path / "registry").get(record.resource_id)
    assert retained.state == "terminated" and retained.receipt.outcome == "terminated"
    assert all(
        ledger.driver.state(p.model_dump()) in {"absent", "different"} for p in record.members
    )


def test_harmless_exit_code_uses_the_pinned_process_handle(tmp_path):
    ledger = ResourceLedger(tmp_path / "registry")
    with (
        ledger.owner(APP_ID) as owner,
        owner.launch(
            [sys.executable, "-c", "import time;time.sleep(1);raise SystemExit(23)"],
            "Harmless exit-code fixture",
            allow_termination=True,
            max_processes=2,
        ) as launch,
    ):
        record = ledger.get(launch.resource_id)
        with ledger.driver.open(record.process.model_dump()) as handle:
            assert handle.wait(5000)
            assert ServiceTree(ledger, launch, handle).poll() == 23


def test_harmless_owner_crash_closes_job_and_leaves_reviewable_orphan_history(tmp_path):
    registry = tmp_path / "orphan-registry"
    marker = tmp_path / "resource-id.txt"
    source = r"""
import os, sys, time
from pathlib import Path
from universal_tools.resources import ResourceLedger
ledger = ResourceLedger(Path(sys.argv[1]))
owner = ledger.owner("rockwell-mcp-server")
launch = owner.launch([sys.executable, "-c", "import time;time.sleep(60)"],
    "Harmless orphan fixture", allow_termination=True, max_processes=2)
launch.refresh()
Path(sys.argv[2]).write_text(launch.resource_id, encoding="utf-8")
os._exit(19)
"""
    # An outer, separately recorded fixture job guarantees test cleanup if the
    # inner owner-loss assertion fails. It contains only these harmless helpers.
    harness = ResourceLedger(tmp_path / "harness-registry")
    with (
        harness.owner("rockwell-fixture-harness") as owner,
        owner.launch(
            [sys.executable, "-c", source, str(registry), str(marker)],
            "Harmless ownership harness",
            allow_termination=True,
            max_processes=4,
        ) as launch,
    ):
        resource_id = await_file(marker)
        record = harness.get(launch.resource_id)
        # The fixture may already have exited; state() still checks full identity.
        deadline = time.monotonic() + 10
        while harness.driver.state(record.process.model_dump()) == "alive":
            if time.monotonic() >= deadline:
                raise AssertionError("Harmless fixture owner did not exit")
            time.sleep(0.02)
        recovered = ResourceLedger(registry)
        retained = recovered.get(resource_id)
        assert recovered.driver.state(retained.owner.model_dump()) in {"absent", "different"}
        assert retained.state == "active" and retained.receipt is None
        assert all(
            recovered.driver.state(p.model_dump()) in {"absent", "different"}
            for p in [retained.process, *retained.members]
        )
        plan = recovered.plan(resource_id)
        assert plan["eligible"]
        receipt = recovered.cleanup(resource_id, plan["revision"], "d" * 32)
        assert receipt["outcome"] == "already_exited"
        assert recovered.cleanup(resource_id, plan["revision"], "d" * 32) == receipt
        # Retained JSON is generic process metadata, with no launch command/body.
        assert "time.sleep" not in json.dumps(recovered.get(resource_id).model_dump(mode="json"))
