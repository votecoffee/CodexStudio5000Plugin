"""App-owned background service tree; never an MCP stdio replacement or PID adopter."""

from __future__ import annotations

import ctypes
import hashlib
import hmac
import json
import os
import re
from contextlib import contextmanager
from pathlib import Path

from universal_tools.resources import ResourceLedger

from .publication import APP_ID, ordinary

OFFLINE_PROFILE = "offline-contained-v1"


def stop_signature(key, owner_id, resource_id, request_id):
    body = "rockwell-stop-v1\n" + owner_id + "\n" + resource_id + "\n" + request_id
    return hmac.new(key.encode("utf-8"), body.encode("utf-8"), hashlib.sha256).hexdigest()


def service_tree_limit(root):
    """Refuse native-enabled profiles until the app's native resource adapter exists."""
    path = ordinary(Path(root) / "service.local.json")
    with path.open("rb") as stream:
        raw = stream.read(1024 * 1024 + 1)
    if len(raw) > 1024 * 1024:
        raise ValueError("Service settings exceed 1 MiB")

    def unique(pairs):
        result = {}
        for key, value in pairs:
            key = key.casefold()
            if key in result:
                raise ValueError("Duplicate service settings key")
            result[key] = value
        return result

    document = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique)
    options = document.get("studio5000") if isinstance(document, dict) else None
    if not isinstance(options, dict):
        raise TypeError("Studio5000 service settings are required")
    for key in ("nativebridgepath", "cipbridgepath"):
        value = options.get(key)
        if value is not None and (not isinstance(value, str) or value.strip()):
            raise ValueError(
                "Native resource registration is not integrated; native paths block this managed profile"
            )
    if (
        options.get("enablecontrolleroperations", False) is not False
        or options.get("controllertargets", []) != []
    ):
        raise ValueError(
            "Native resource registration is not integrated; controller settings block this managed profile"
        )
    maximum = options.get("maximumownedprocesses", 32)
    if type(maximum) is not int or not 1 <= maximum <= 128:
        raise ValueError("MaximumOwnedProcesses must be an integer between 1 and 128")
    return maximum


def windows_exit_code(process):
    """Read the held, identity-checked handle, never reopen a recyclable PID."""
    from ctypes import wintypes

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    function = kernel.GetExitCodeProcess
    function.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    function.restype = wintypes.BOOL
    result = wintypes.DWORD()
    if not function(process.handle, ctypes.byref(result)):
        raise ctypes.WinError(ctypes.get_last_error())
    return result.value


class ServiceTree:
    def __init__(self, ledger, launch, handle, *, exit_code=None):
        self.ledger, self.launch, self.handle = ledger, launch, handle
        self.exit_code = exit_code or windows_exit_code

    @property
    def pid(self):
        return self.launch.pid

    def poll(self):
        return None if self.handle.alive() else self.exit_code(self.handle)

    def refresh(self):
        return self.launch.refresh()

    def stop_requested(self, root):
        path = ordinary(Path(root) / ".local/service-stop-request.json")
        try:
            with path.open("rb") as stream:
                raw = stream.read(8193)
        except FileNotFoundError:
            return False
        if len(raw) > 8192:
            return False
        try:
            request = json.loads(raw.decode("utf-8-sig"))
        except (ValueError, UnicodeError):
            return False
        if not isinstance(request, dict) or set(request) != {
            "schemaVersion",
            "ownerId",
            "resourceId",
            "requestId",
            "signature",
        }:
            return False
        if type(request["schemaVersion"]) is not int or request["schemaVersion"] != 1:
            return False
        if not isinstance(request["signature"], str) or not re.fullmatch(
            "[0-9a-f]{64}", request["signature"]
        ):
            return False
        if any(
            not isinstance(request[k], str) or not re.fullmatch("[0-9a-f]{32}", request[k])
            for k in ("ownerId", "resourceId", "requestId")
        ):
            return False
        record = self.ledger.get(self.launch.resource_id)
        if request["ownerId"] != record.owner_id or request["resourceId"] != record.resource_id:
            return False
        key_path = ordinary(Path(root) / ".local/service.key")
        try:
            with key_path.open("rb") as stream:
                key = stream.read(4097)
            key = key.decode("utf-8-sig").strip()
        except (OSError, UnicodeError):
            return False
        return 32 <= len(key) <= 4096 and hmac.compare_digest(
            request["signature"],
            stop_signature(key, record.owner_id, record.resource_id, request["requestId"]),
        )

    def receipt_metadata(self):
        record = self.ledger.get(self.launch.resource_id)
        return {
            "schemaVersion": 2,
            "pid": record.owner.pid,
            "servicePid": record.process.pid,
            "python": record.owner.executable,
            "ownerId": record.owner_id,
            "resourceId": record.resource_id,
            "resourceRegistry": str(self.ledger.directory.parent),
            "resourceProfile": OFFLINE_PROFILE,
        }


@contextmanager
def launch_service_tree(root, *, registry=None, ledger_factory=ResourceLedger):
    root = ordinary(root)
    if (root / ".local/maintenance-hold.json").exists():
        raise ValueError("Operator maintenance hold requires explicit human release before startup")
    maximum = service_tree_limit(root)
    executable = ordinary(root / "artifacts/app/Service/Studio5000.Service.exe")
    ledger = ledger_factory(registry)
    # This context stays in the actual long-lived supervisor. No registration
    # helper, inherited stdio or temporary owner generation is involved.
    with (
        ledger.owner(APP_ID) as owner,
        owner.launch(
            [str(executable)],
            "Rockwell background service and inherited workers",
            allow_termination=True,
            cwd=root,
            env={
                **os.environ,
                "STUDIO5000_PLUGIN_ROOT": str(root),
                "ROCKWELL_MANAGED_SERVICE": "1",
                "ROCKWELL_RESOURCE_PROFILE": OFFLINE_PROFILE,
            },
            max_processes=maximum,
        ) as launch,
    ):
        record = launch.refresh()
        with ledger.driver.open(record.process.model_dump()) as handle:
            yield ServiceTree(ledger, launch, handle)
        # OwnedLaunch records a single durable cleanup intent before effect.
        # Uncertain outcomes remain retained; this app never substitutes a
        # new cleanup request or clears a barrier automatically.
