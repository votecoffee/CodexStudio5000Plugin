"""Supervisor orchestration with stubs only: no service, manager or network."""

import json
from contextlib import contextmanager, nullcontext

import pytest


class Child:
    pid = 123  # Synthetic; never looked up by these tests.

    def __init__(self):
        self.closes = 0
        self.polls = iter([None, 7])

    def poll(self):
        return next(self.polls)

    def refresh(self):
        raise ValueError("synthetic ledger failure")

    def confirm_exit(self, root):
        return True

    def receipt_metadata(self):
        return {"schemaVersion": 2, "resourceId": "synthetic"}

    def stop_requested(self, root):
        return False

    def request_stop(self, root):
        self.stop_sent = True

    def forward_stop_result(self, root):
        pass


@pytest.fixture
def supervisor(tmp_path, monkeypatch):
    from rockwell_framework import management

    (tmp_path / ".local").mkdir()
    (tmp_path / "service.local.json").write_text('{"Studio5000":{}}', encoding="utf-8")
    child = Child()
    monkeypatch.setattr(management, "check_versions", lambda: None)
    monkeypatch.setattr(management, "installation", lambda _: {"pluginId": "synthetic"})
    monkeypatch.setattr(management, "versions", lambda _: {})
    monkeypatch.setattr(management, "InstanceLock", lambda _: nullcontext())
    monkeypatch.setattr(management, "managed_service", lambda *a: nullcontext())
    monkeypatch.setattr(management.httpx, "Client", lambda **k: nullcontext(object()))
    monkeypatch.setattr(management.time, "sleep", lambda _: None)

    @contextmanager
    def launch(*args, **kwargs):
        try:
            yield child
        finally:
            child.closes += 1

    monkeypatch.setattr(management, "launch_service_tree", launch)
    return management, child, tmp_path


def test_launch_receipt_failure_still_closes_the_owned_tree(supervisor, monkeypatch):
    management, child, root = supervisor

    def fail_receipt(*args):
        raise OSError("synthetic receipt failure")

    monkeypatch.setattr(management, "atomic_json", fail_receipt)
    with pytest.raises(OSError, match="receipt failure"):
        management.run(root)
    assert child.closes == 1


def test_reporting_failure_keeps_serving_until_observed_exit(supervisor, monkeypatch):
    management, child, root = supervisor
    monkeypatch.setattr(management, "poll_activity", lambda *a: (_ for _ in ()).throw(OSError()))
    assert management.run(root) == 7
    assert child.closes == 1
    saved = json.loads((root / ".local/management-supervisor.json").read_text())
    assert saved["schemaVersion"] == 2 and saved["resourceId"] == "synthetic"


def test_ledger_failure_keeps_owner_until_verified_exit_without_effect_replay(
    supervisor, monkeypatch
):
    management, child, root = supervisor
    times = iter([0, 3])
    monkeypatch.setattr(management.time, "monotonic", lambda: next(times))
    assert management.run(root) == 7
    assert child.closes == 1


def test_owner_stop_request_closes_once_without_a_pid_kill(supervisor, monkeypatch):
    management, child, root = supervisor
    monkeypatch.setattr(child, "stop_requested", lambda _: True)
    assert management.run(root) == 7
    assert child.stop_sent
    assert child.closes == 1


def test_unverified_exit_keeps_the_surviving_owner_until_close_readback(supervisor, monkeypatch):
    management, child, root = supervisor
    child.polls = iter([7, 7])
    confirmed = iter([False, True])
    monkeypatch.setattr(child, "confirm_exit", lambda _: next(confirmed))
    waits = []

    def observe(_):
        assert child.closes == 0
        waits.append(True)

    monkeypatch.setattr(management.time, "sleep", observe)
    assert management.run(root) == 7
    assert len(waits) == 1 and child.closes == 1


def test_ctrl_c_requests_owned_close_without_unwinding_surviving_owner(supervisor, monkeypatch):
    import signal

    management, child, root = supervisor
    child.polls = iter([None, None, 7])
    previous = signal.getsignal(signal.SIGINT)

    def interrupt(_):
        signal.getsignal(signal.SIGINT)(signal.SIGINT, None)
        assert child.closes == 0

    monkeypatch.setattr(management.time, "sleep", interrupt)
    assert management.run(root) == 7
    assert child.stop_sent and child.closes == 1
    assert signal.getsignal(signal.SIGINT) is previous


def test_held_supervisor_never_registers_or_launches(tmp_path, monkeypatch):
    from rockwell_framework import management

    local = tmp_path / ".local"
    local.mkdir()
    (local / "maintenance-hold.json").write_text("{}", encoding="utf-8")
    monkeypatch.setattr(management, "check_versions", lambda: None)
    monkeypatch.setattr(management, "installation", lambda _: pytest.fail("Read installation"))
    monkeypatch.setattr(management, "launch_service_tree", lambda *a, **k: pytest.fail("Launch"))
    with pytest.raises(ValueError, match="maintenance hold"):
        management.run(tmp_path)


def test_invalid_limit_refuses_before_registration(tmp_path, monkeypatch):
    from rockwell_framework import management

    (tmp_path / ".local").mkdir()
    (tmp_path / "service.local.json").write_text(
        '{"Studio5000":{"MaximumOwnedProcesses":true}}', encoding="utf-8"
    )
    monkeypatch.setattr(management, "check_versions", lambda: None)
    monkeypatch.setattr(management, "installation", lambda _: pytest.fail("Read installation"))
    with pytest.raises(ValueError, match="MaximumOwnedProcesses"):
        management.run(tmp_path)


@pytest.mark.parametrize("command", ["serve", "frontend"])
def test_held_optional_front_doors_never_connect_or_start(tmp_path, monkeypatch, command):
    from rockwell_framework import __main__ as cli

    local = tmp_path / ".local"
    local.mkdir()
    (local / "maintenance-hold.json").write_text("{}", encoding="utf-8")
    monkeypatch.setattr("sys.argv", ["synthetic", "--state", str(tmp_path), command, "--skills"])
    monkeypatch.setattr(cli, "check_versions", lambda: None)
    monkeypatch.setattr(cli, "verify", lambda _: {"app_root": str(tmp_path)})
    monkeypatch.setattr(cli, "profile", lambda _: {"app_root": str(tmp_path)})
    monkeypatch.setattr(cli, "GuidanceSource", lambda _: pytest.fail("Connected guidance"))
    monkeypatch.setattr(cli, "serve_application", lambda *a, **k: pytest.fail("Started server"))
    monkeypatch.setattr(cli, "create_surface", lambda *a, **k: pytest.fail("Started frontend"))
    with pytest.raises(ValueError, match="maintenance hold"):
        cli.main()
