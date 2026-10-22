"""Real ledger bytes with a synthetic process driver; no process or host launches."""

import json
import os
from contextlib import contextmanager

import pytest
from universal_tools.resources import ProcessIdentity, ResourceLedger

from rockwell_framework.ownership import (
    OFFLINE_PROFILE,
    launch_service_tree,
    service_tree_limit,
    stop_signature,
)


class Driver:
    def __init__(self):
        self.items = {}
        self.identity(os.getpid(), "100", "supervisor")
        self.created = []
        self.groups = []
        self.fail_stop = False

    def identity(self, pid, created, name):
        result = ProcessIdentity(
            host_id="a" * 64, pid=pid, created=created, executable="C:/fixture/" + name + ".exe"
        ).model_dump()
        self.items[pid] = result
        return result

    def capture(self, pid):
        return self.items[pid]

    def state(self, identity):
        current = self.items.get(identity["pid"])
        return "absent" if current is None else "alive" if current == identity else "different"

    @contextmanager
    def open(self, identity):
        assert self.state(identity) == "alive"
        driver = self

        class Handle:
            def alive(self):
                return driver.state(identity) == "alive"

            def terminate(self):
                pytest.fail("App must close its contained group, not kill a guessed PID")

        yield Handle()

    def create_suspended(self, argv, **options):
        self.created.append((argv, options))
        driver = self
        identity = self.identity(900001, "200", "worker")

        class Group:
            pid = identity["pid"]

            def __init__(self):
                self.identity = identity
                self.resumed = False
                self.closed = False
                self.stops = 0

            def resume(self):
                self.resumed = True

            def identities(self):
                return [identity] if driver.state(identity) == "alive" else []

            def stop(self):
                self.stops += 1
                driver.items.pop(self.pid, None)
                if driver.fail_stop:
                    raise OSError("synthetic lost cleanup result")

            def close(self):
                self.closed = True
                driver.items.pop(self.pid, None)

        group = Group()
        self.groups.append(group)
        return group


@pytest.fixture
def app(tmp_path):
    root = tmp_path / "app"
    root.mkdir()
    (root / "service.local.json").write_text('{"Studio5000":{}}', encoding="utf-8")
    driver = Driver()
    registry = tmp_path / "private-fixture-registry"
    ledger = ResourceLedger(registry, driver=driver)
    return root, driver, ledger, registry


@pytest.mark.parametrize(
    "options",
    [
        {"NativeBridgePath": "native.exe"},
        {"CipBridgePath": "cip.exe"},
        {"EnableControllerOperations": True},
        {"EnableControllerOperations": "false"},
        {"ControllerTargets": [{"Id": "fixture"}]},
        {"ControllerTargets": None},
        {"MaximumOwnedProcesses": 0},
        {"MaximumOwnedProcesses": 129},
        {"MaximumOwnedProcesses": True},
        {"MaximumOwnedProcesses": "4"},
    ],
)
def test_unassessed_native_or_invalid_policy_refuses_before_ledger_creation(app, options):
    root, driver, ledger, registry = app
    (root / "service.local.json").write_text(json.dumps({"Studio5000": options}), encoding="utf-8")
    with (
        pytest.raises(ValueError),
        launch_service_tree(root, registry=registry, ledger_factory=lambda _: ledger),
    ):
        pytest.fail("Unassessed profile was launched")
    assert driver.created == []
    assert not ledger.directory.exists()


def test_duplicate_settings_are_rejected_and_bom_casefold_and_bounds_are_supported(app):
    root, _, _, _ = app
    path = root / "service.local.json"
    path.write_text('{"STUDIO5000":{"MaximumOwnedProcesses":3}}', encoding="utf-8-sig")
    assert service_tree_limit(root) == 3
    path.write_text('{"Studio5000":{"MaximumOwnedProcesses":2,"maximumownedprocesses":3}}')
    with pytest.raises(ValueError, match="Duplicate"):
        service_tree_limit(root)


def test_service_root_record_is_owned_by_surviving_supervisor_and_has_durable_cleanup(
    app, monkeypatch
):
    root, driver, ledger, registry = app
    monkeypatch.setattr("rockwell_framework.ownership.windows_exit_code", lambda _: 7)
    with launch_service_tree(root, registry=registry, ledger_factory=lambda _: ledger) as child:
        record = ledger.records()[0]
        assert record.owner.pid == os.getpid()
        assert record.ownership == "private" and record.containment == "windows_job"
        assert record.cleanup_mode == "terminate" and record.members == [record.process]
        assert record.state == "active" and driver.groups[0].resumed
        assert child.poll() is None
        saved = child.receipt_metadata()
        assert saved["resourceId"] == record.resource_id
        assert saved["ownerId"] == record.owner_id and saved["schemaVersion"] == 2
        argv, options = driver.created[0]
        assert argv == [str(root / "artifacts/app/Service/Studio5000.Service.exe")]
        assert options["env"]["ROCKWELL_RESOURCE_PROFILE"] == OFFLINE_PROFILE
        assert "stdout" not in options and "stdin" not in options
        driver.items.pop(child.pid)
        assert child.poll() == 7
    retained = ResourceLedger(registry, driver=driver).get(record.resource_id)
    assert retained.state == "terminated" and retained.receipt.outcome == "terminated"
    assert driver.groups[0].closed and driver.groups[0].stops == 1


def test_identity_journal_failure_closes_suspended_child_without_execution(app, monkeypatch):
    root, driver, ledger, registry = app
    save = ledger._save

    def fail_active(record):
        if record.state == "active":
            raise OSError("synthetic identity write failure")
        save(record)

    monkeypatch.setattr(ledger, "_save", fail_active)
    with (
        pytest.raises(OSError, match="identity write failure"),
        launch_service_tree(root, registry=registry, ledger_factory=lambda _: ledger),
    ):
        pytest.fail("Worker executed before identity was durable")
    assert not driver.groups[0].resumed and driver.groups[0].closed
    assert ledger.records()[0].state == "failed"


def test_uncertain_cleanup_is_retained_and_not_retried(app):
    root, driver, ledger, registry = app
    driver.fail_stop = True
    with (
        pytest.raises(ValueError, match="recovery blocks"),
        launch_service_tree(root, registry=registry, ledger_factory=lambda _: ledger),
    ):
        pass
    retained = ResourceLedger(registry, driver=driver).records()[0]
    assert retained.state == "uncertain" and retained.receipt.outcome == "uncertain"
    assert driver.groups[0].stops == 1
    with pytest.raises(ValueError, match="already claimed"):
        ledger.cleanup(retained.resource_id, retained.revision, "b" * 32)
    assert driver.groups[0].stops == 1


def test_private_stop_signature_has_a_cross_language_fixture_vector():
    assert (
        stop_signature("k" * 64, "a" * 32, "b" * 32, "c" * 32)
        == "3a28490c7a446fe3a0a8d0319b2abbd9cf4e4fae9d9354bede5ee0b31682678a"
    )


def test_stop_intent_requires_exact_generation_and_signature_without_exposing_key(app):
    root, _, ledger, registry = app
    local = root / ".local"
    local.mkdir()
    key = "k" * 64
    (local / "service.key").write_text(key, encoding="utf-8")
    path = local / "service-stop-request.json"
    with launch_service_tree(root, registry=registry, ledger_factory=lambda _: ledger) as child:
        assert not child.stop_requested(root)
        record = ledger.get(child.launch.resource_id)
        valid = {
            "schemaVersion": 1,
            "ownerId": record.owner_id,
            "resourceId": record.resource_id,
            "requestId": "c" * 32,
        }
        valid["signature"] = stop_signature(
            key, record.owner_id, record.resource_id, valid["requestId"]
        )
        for changes in (
            {"signature": "é"},
            {"signature": "0" * 64},
            {"ownerId": "0" * 32},
            {"schemaVersion": True},
        ):
            path.write_text(json.dumps({**valid, **changes}), encoding="utf-8")
            assert not child.stop_requested(root)
        path.write_text(json.dumps(valid), encoding="utf-8")
        assert child.stop_requested(root)
        assert key not in path.read_text("utf-8")
