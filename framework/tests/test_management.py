import concurrent.futures
import json
import secrets

import httpx
import pytest
from universal_tools.management import ManagementClient, RestartResult

from rockwell_framework.management import (
    RESTART_REASON,
    CachedStatus,
    component,
    managed_service,
    versions,
)


def activity(count=0, version="0.35.5"):
    return {
        "schema_version": 1,
        "name": "RockwellMCPServer",
        "version": version,
        "active_operations": count,
    }


def cache():
    value = CachedStatus({"app": component("0.35.5", available="0.35.6")})
    value.update(activity(), value.snapshot().versions)
    return value


def test_cached_callback_is_thread_safe_and_does_not_read_projects(monkeypatch):
    from rockwell_framework import management

    value = cache()
    monkeypatch.setattr(
        management, "read_metadata", lambda _: pytest.fail("Status callback performed I/O")
    )
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        snapshots = list(executor.map(lambda _: value.snapshot(), range(64)))
    assert all(s.state == "running" and not s.restart_allowed for s in snapshots)
    assert all(s.restart_reason == RESTART_REASON for s in snapshots)
    snapshots[0].versions.clear()
    assert value.snapshot().versions["app"].running == "0.35.5"


def test_stale_busy_unavailable_and_invalid_activity_remain_nonrestartable():
    now = [0]
    value = CachedStatus({"app": component("0.35.5")}, clock=lambda: now[0])
    value.update(activity(2), value.snapshot().versions)
    assert value.snapshot().state == "busy" and value.snapshot().active_operations == 2
    now[0] = 6
    assert value.snapshot().state == "degraded" and not value.snapshot().restart_allowed
    value.unavailable()
    with pytest.raises(ValueError):
        value.update(activity(-1), value.snapshot().versions)
    assert not value.snapshot().restart_allowed


def test_versions_distinguish_local_installed_running_available_and_unknown(tmp_path):
    local = tmp_path / ".local"
    local.mkdir()
    (local / "management-installation.json").write_text(
        json.dumps(
            {
                "pluginId": "rockwell-mcp-server@rockwell-mcp-server-local",
                "appVersion": "0.35.5",
                "pluginVersion": "0.35.4",
                "mcpDotnetSdk": "2.2.0",
            }
        )
    )
    (local / "management-runtime.json").write_text(
        json.dumps({"frameworkVersion": "0.18.0", "mcpPythonSdk": "2.3.0"})
    )
    (tmp_path / "plugin.json").write_text('{"version":"0.35.6"}')
    current = versions(tmp_path)
    assert current["app"].installed == "0.35.5" and current["app"].update_pending
    assert current["plugin"].running is None and current["plugin"].update_pending
    assert current["framework"].running == "0.18.0" and current["mcp-python"].running == "2.3.0"
    assert current["mcp-dotnet"].available is None
    assert component(None).installed == "unknown" and component(None).available is None


def test_real_http_authentication_private_snapshot_and_clean_shutdown(tmp_path):
    service = managed_service(cache(), "rockwell-mcp-server@rockwell-mcp-server-local", tmp_path)
    client = ManagementClient(tmp_path)
    with service:
        row = client.snapshot()["services"][0]
        assert row["plugin_id"] == "rockwell-mcp-server@rockwell-mcp-server-local"
        assert row["status"]["state"] == "running" and not row["status"]["restart_allowed"]
        assert "atomic restart fence" in row["status"]["restart_reason"]
        assert "token" not in json.dumps(row) and "endpoint" not in json.dumps(row)
        with httpx.Client(trust_env=False) as http:
            assert http.get(service.record.endpoint + "/status").status_code == 401
            headers = {"Authorization": "Bearer " + service.token, "Origin": "http://example.test"}
            assert http.get(service.record.endpoint + "/status", headers=headers).status_code == 401
    assert client.snapshot()["services"][0]["status"]["state"] == "stopped"


def test_duplicate_concurrent_stale_and_busy_requests_have_zero_effects(tmp_path):
    current = cache()
    service = managed_service(current, "rockwell-mcp-server@local", tmp_path)
    effects = []
    service.restart_callback = lambda request: (
        effects.append(request.request_id)
        or RestartResult(outcome="accepted", message="Unexpected effect")
    )
    client = ManagementClient(tmp_path)
    with service:
        row = client.snapshot()["services"][0]
        key = secrets.token_hex(16)

        def request(_):
            return client.restart(row["app_id"], row["instance_id"], row["revision"], key)

        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
            results = list(executor.map(request, range(8)))
        assert all(result == results[0] and result["outcome"] == "blocked" for result in results)
        current.update(activity(1), current.snapshot().versions)
        assert (
            client.restart(
                row["app_id"], row["instance_id"], row["revision"], secrets.token_hex(16)
            )["outcome"]
            == "blocked"
        )
        changed = client.restart(row["app_id"], row["instance_id"], "f" * 64, key)
        assert changed["outcome"] == "uncertain" and effects == []


def test_durable_uncertain_restart_intent_survives_instance_replacement(tmp_path):
    first = managed_service(cache(), "rockwell-mcp-server@local", tmp_path)
    key = secrets.token_hex(16)
    with first:
        first.journal.claim(first.app_id, "a" * 64, key)  # Synthetic crash before a receipt.
    second = managed_service(cache(), "rockwell-mcp-server@local", tmp_path)
    with second:
        assert first.instance_id != second.instance_id
        assert second.pending_restarts()[0]["request_id"] == key
        assert "reconciliation" in second.snapshot()["status"]["restart_reason"]
        # Only the app test attests its independently verified zero-effect outcome.
        second.reconcile_restart(
            key, RestartResult(outcome="blocked", message="Verified no restart effect")
        )
        assert not second.pending_restarts()
        assert not second.snapshot()["status"]["restart_allowed"]
