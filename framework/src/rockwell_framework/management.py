"""App-owned reporting supervisor for the existing .NET service; no tray restart effects."""

from __future__ import annotations

import argparse
import importlib.metadata
import ipaddress
import json
import os
import subprocess
import threading
import time
from pathlib import Path
from urllib.parse import urlsplit

import httpx
from universal_tools.daemon import InstanceLock
from universal_tools.management import (
    ComponentVersion,
    ManagedService,
    ManagedStatus,
    RestartResult,
)
from universal_tools.models import atomic_json

from .publication import APP_ID, baseline, check_versions, ordinary

RESTART_REASON = (
    "No atomic restart fence for all chats, unsaved GUI documents, prepared reviews and "
    "unresolved native effects. Use the operator-managed update workflow."
)


def read_metadata(path):
    path = ordinary(path)
    with path.open("rb") as stream:
        raw = stream.read(32769)
    if len(raw) > 32768:
        raise ValueError("Installation metadata exceeds its limit")
    return json.loads(raw.decode("utf-8-sig"))


def component(installed, running=None, available=None):
    if any(
        value is not None and (not isinstance(value, str) or not 1 <= len(value) <= 128)
        for value in (installed, running, available)
    ):
        raise ValueError("Invalid component version metadata")
    installed = installed or "unknown"
    pending = bool(running and installed != "unknown" and running != installed)
    if available and installed != "unknown" and available != installed:
        try:
            pending |= tuple(map(int, available.split("."))) > tuple(map(int, installed.split(".")))
        except ValueError:
            pending = True
    return ComponentVersion(
        installed=installed, running=running, available=available, update_pending=pending
    )


def installation(root):
    stamp = read_metadata(root / ".local/management-installation.json")
    plugin_id = stamp.get("pluginId")
    if not isinstance(plugin_id, str) or not plugin_id.startswith("rockwell-mcp-server@"):
        raise ValueError("Install the owned standalone plugin before enabling management")
    return stamp


def versions(root):
    stamp = installation(root)
    source = read_metadata(root / "plugin.json").get("version")
    selected = read_metadata(root / ".local/management-runtime.json")
    return {
        "app": component(stamp.get("appVersion"), available=source),
        # Loaded chat/plugin versions are not observable from this service.
        "plugin": component(stamp.get("pluginVersion"), available=source),
        "management-agent": component(
            selected.get("appPackageVersion")
            or importlib.metadata.version("rockwell-framework-host"),
            importlib.metadata.version("rockwell-framework-host"),
            source,
        ),
        "framework": component(
            selected.get("frameworkVersion"),
            importlib.metadata.version("universal-codex-tools"),
            baseline()["version"],
        ),
        "mcp-python": component(
            selected.get("mcpPythonSdk"),
            importlib.metadata.version("mcp"),
            baseline()["mcp_python_sdk"],
        ),
        "mcp-dotnet": component(stamp.get("mcpDotnetSdk")),
    }


class CachedStatus:
    def __init__(self, installed_versions, *, clock=time.monotonic, stale_seconds=5):
        self.lock = threading.Lock()
        self.clock, self.stale_seconds = clock, stale_seconds
        self.updated = clock()
        self.value = ManagedStatus(
            state="starting",
            message="Starting the shared .NET service",
            versions=installed_versions,
            restart_allowed=False,
            restart_reason=RESTART_REASON,
        )

    def update(self, activity, installed_versions):
        count = activity.get("active_operations")
        version = activity.get("version")
        if (
            activity.get("schema_version") != 1
            or activity.get("name") != "RockwellMCPServer"
            or type(count) is not int
            or not 0 <= count <= 1000000
            or not isinstance(version, str)
            or not 1 <= len(version) <= 128
        ):
            raise ValueError("Invalid service activity metadata")
        installed_versions = dict(installed_versions)
        app = installed_versions["app"]
        installed_versions["app"] = component(app.installed, version, app.available)
        with self.lock:
            self.value = ManagedStatus(
                state="busy" if count else "running",
                active_operations=count,
                message="Shared .NET service; tray management is reporting only",
                versions=installed_versions,
                restart_allowed=False,
                restart_reason=RESTART_REASON,
            )
            self.updated = self.clock()

    def unavailable(self):
        with self.lock:
            self.value = self.value.model_copy(
                update={
                    "state": "degraded",
                    "message": "Shared service status is unavailable",
                    "restart_allowed": False,
                    "restart_reason": RESTART_REASON,
                }
            )

    def snapshot(self):
        with self.lock:
            result = self.value.model_copy(deep=True)
            if self.clock() - self.updated > self.stale_seconds:
                return result.model_copy(
                    update={
                        "state": "degraded",
                        "message": "Shared service status is stale",
                        "restart_allowed": False,
                    }
                )
            return result


def deny_restart(_request):
    # Defense in depth: ManagedService denies first because restart_allowed is always false.
    return RestartResult(outcome="blocked", message=RESTART_REASON)


def managed_service(cache, plugin_id, registry=None):
    return ManagedService(
        APP_ID,
        "RockwellMCPServer",
        cache.snapshot,
        restart=deny_restart,
        plugin_id=plugin_id,
        registry=registry,
    )


def poll_activity(root, client):
    connection = read_metadata(root / ".local/connection.json")
    url = connection["url"]
    parsed = urlsplit(url)
    if (
        parsed.scheme != "http"
        or not ipaddress.ip_address(parsed.hostname).is_loopback
        or parsed.path not in ("", "/")
    ):
        raise ValueError("Service endpoint must be loopback HTTP")
    token = (ordinary(root / ".local/service.key")).read_text("utf-8-sig").strip()
    with client.stream(
        "GET",
        url.rstrip("/") + "/api/management/activity",
        headers={"Authorization": "Bearer " + token},
    ) as response:
        response.raise_for_status()
        raw = bytearray()
        for chunk in response.iter_bytes():
            raw.extend(chunk)
            if len(raw) > 32768:
                raise ValueError("Service activity exceeds its limit")
    return json.loads(raw)


def stop_owned_child(child):
    """Close only this invocation's Popen handle; saved PID files are not authority."""
    if child is None or child.poll() is not None:
        return
    child.terminate()
    try:
        child.wait(timeout=10)
    except subprocess.TimeoutExpired:
        child.kill()
        child.wait(timeout=5)


def run(root, *, registry=None):
    check_versions()
    root = ordinary(root)
    local = ordinary(root / ".local")
    if (local / "maintenance-hold.json").exists():
        raise ValueError("Operator maintenance hold requires explicit human release before startup")
    stamp = installation(root)
    executable = ordinary(root / "artifacts/app/Service/Studio5000.Service.exe")
    child = None
    cache = CachedStatus(versions(root))
    with (
        InstanceLock(local / "management-supervisor.lock"),
        managed_service(cache, stamp["pluginId"], registry),
        (local / "service.stdout.log").open("w", encoding="utf-8") as out,
        (local / "service.stderr.log").open("w", encoding="utf-8") as err,
    ):
        try:
            child = subprocess.Popen(
                [str(executable)],
                cwd=root,
                stdout=out,
                stderr=err,
                env={
                    **os.environ,
                    "STUDIO5000_PLUGIN_ROOT": str(root),
                    "ROCKWELL_MANAGED_SERVICE": "1",
                },
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            atomic_json(local / "service.pid", child.pid)
            atomic_json(
                local / "management-supervisor.json",
                {
                    "schemaVersion": 1,
                    "pid": os.getpid(),
                    "servicePid": child.pid,
                    "python": str(Path(os.sys.executable).resolve()),
                },
            )
            with httpx.Client(timeout=1, trust_env=False, follow_redirects=False) as client:
                while child.poll() is None:
                    try:
                        cache.update(poll_activity(root, client), versions(root))
                    except Exception:  # noqa: BLE001 - status failures must not stop engineering work
                        # Reporting failures cannot interrupt the independently serving app.
                        cache.unavailable()
                    time.sleep(0.5)
            return child.returncode
        except KeyboardInterrupt:
            return 130
        finally:
            # Own only the child created in this invocation. Process loss is not
            # reconciliation of native effects; existing durable barriers remain.
            stop_owned_child(child)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-root", type=Path, required=True)
    parser.add_argument("--registry", type=Path)
    args = parser.parse_args()
    raise SystemExit(run(args.app_root, registry=args.registry))


if __name__ == "__main__":
    main()
