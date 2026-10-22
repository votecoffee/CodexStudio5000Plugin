"""Pure stub tests: no service, manager registration, process or network listener."""

import subprocess
from contextlib import nullcontext

import pytest

from rockwell_framework.management import stop_owned_child


class Child:
    def __init__(self, *, exited=False, stall=False):
        self.exited = exited
        self.stall = stall
        self.calls = []

    def poll(self):
        return 0 if self.exited else None

    def terminate(self):
        self.calls.append("terminate")

    def kill(self):
        self.calls.append("kill")

    def wait(self, timeout):
        self.calls.append(("wait", timeout))
        if self.stall and timeout == 10:
            raise subprocess.TimeoutExpired("synthetic", timeout)
        self.exited = True
        return 0


def test_absent_or_already_exited_child_is_untouched():
    stop_owned_child(None)
    child = Child(exited=True)
    stop_owned_child(child)
    assert child.calls == []


def test_only_supplied_owned_child_is_stopped_and_cleanup_is_idempotent():
    owned, unrelated = Child(), Child()
    stop_owned_child(owned)
    stop_owned_child(owned)
    assert owned.calls == ["terminate", ("wait", 10)]
    assert unrelated.calls == []


def test_stalled_owned_child_has_bounded_escalation():
    child = Child(stall=True)
    stop_owned_child(child)
    assert child.calls == ["terminate", ("wait", 10), "kill", ("wait", 5)]


def test_unresolved_cleanup_error_is_not_reported_as_success():
    class Refused(Child):
        def terminate(self):
            raise PermissionError("synthetic cleanup refusal")

    with pytest.raises(PermissionError, match="cleanup refusal"):
        stop_owned_child(Refused())


def test_launch_receipt_failure_still_closes_only_the_created_child(tmp_path, monkeypatch):
    from rockwell_framework import management

    (tmp_path / ".local").mkdir()
    child = Child()
    monkeypatch.setattr(management, "check_versions", lambda: None)
    monkeypatch.setattr(management, "installation", lambda _: {"pluginId": "synthetic"})
    monkeypatch.setattr(management, "versions", lambda _: {})
    monkeypatch.setattr(management, "InstanceLock", lambda _: nullcontext())
    monkeypatch.setattr(management, "managed_service", lambda *args: nullcontext())

    def launch(args, **kwargs):
        assert kwargs["env"]["ROCKWELL_MANAGED_SERVICE"] == "1"
        child.pid = 123  # Stub metadata only; no PID lookup or process is created.
        return child

    def fail_receipt(*args):
        raise OSError("synthetic receipt failure")

    monkeypatch.setattr(management.subprocess, "Popen", launch)
    monkeypatch.setattr(management, "atomic_json", fail_receipt)
    with pytest.raises(OSError, match="receipt failure"):
        management.run(tmp_path)
    assert child.calls == ["terminate", ("wait", 10)]


def test_held_supervisor_never_registers_or_launches(tmp_path, monkeypatch):
    from rockwell_framework import management

    local = tmp_path / ".local"
    local.mkdir()
    (local / "maintenance-hold.json").write_text("{}", encoding="utf-8")
    monkeypatch.setattr(management, "check_versions", lambda: None)
    monkeypatch.setattr(management, "installation", lambda _: pytest.fail("Read installation"))
    monkeypatch.setattr(management.subprocess, "Popen", lambda *a, **k: pytest.fail("Launch"))
    with pytest.raises(ValueError, match="maintenance hold"):
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
