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

from universal_tools.models import atomic_json
from universal_tools.resources import ProcessIdentity, ResourceLedger

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
        self.stop_request_id = None
        self.stop_owner_id = None
        self.stop_delivered = set()

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
        if request["requestId"] in self.stop_delivered:
            return False
        key_path = ordinary(Path(root) / ".local/service.key")
        try:
            with key_path.open("rb") as stream:
                key = stream.read(4097)
            key = key.decode("utf-8-sig").strip()
        except (OSError, UnicodeError):
            return False
        verified = 32 <= len(key) <= 4096 and hmac.compare_digest(
            request["signature"],
            stop_signature(key, record.owner_id, record.resource_id, request["requestId"]),
        )
        if verified:
            self.stop_request_id = request["requestId"]
        return verified

    def request_stop(self, root, request_id=None):
        from uuid import uuid4

        root = ordinary(root)
        intent_id = request_id or self.stop_request_id or uuid4().hex
        if intent_id in self.stop_delivered:
            return
        path = ordinary(root / ".local/service-owner.json")
        with path.open("rb") as stream:
            raw = stream.read(32769)
        if len(raw) > 32768:
            raise ValueError("Native service ownership receipt exceeds its bound")
        receipt = json.loads(raw.decode("utf-8-sig"))
        record = self.ledger.get(self.launch.resource_id)
        if (
            receipt.get("schemaVersion") != 1
            or ProcessIdentity.model_validate(receipt.get("owner")) != record.process
            or not re.fullmatch("[0-9a-f]{32}", receipt.get("ownerId", ""))
        ):
            raise ValueError(
                "Service ownership generation does not match the exact launched process"
            )
        with ordinary(root / ".local/service.key").open("rb") as stream:
            key = stream.read(4097).decode("utf-8-sig").strip()
        if not 32 <= len(key) <= 4096:
            raise ValueError("Private stop authentication is unavailable")
        service_owner = receipt["ownerId"]
        atomic_json(
            root / ".local/service-owner-stop.json",
            {
                "schemaVersion": 1,
                "ownerId": service_owner,
                "resourceId": service_owner,
                "requestId": intent_id,
                "signature": stop_signature(key, service_owner, service_owner, intent_id),
            },
        )
        self.stop_owner_id, self.stop_request_id = service_owner, intent_id
        self.stop_delivered.add(intent_id)

    def forward_stop_result(self, root):
        if self.stop_owner_id is None:
            return
        root = ordinary(root)
        path = ordinary(root / ".local/service-owner-stop-result.json")
        if not path.exists():
            return
        with path.open("rb") as stream:
            raw = stream.read(8193)
        if len(raw) > 8192:
            raise ValueError("Service stop result exceeds its bound")
        response = json.loads(raw.decode("utf-8-sig"))
        if (
            response.get("ownerId") != self.stop_owner_id
            or response.get("requestId") != self.stop_request_id
        ):
            return
        if response.get("state") not in {"reviewing", "blocked", "uncertain", "completed"}:
            raise ValueError("Invalid owner stop state")
        record = self.ledger.get(self.launch.resource_id)
        atomic_json(
            root / ".local/service-stop-result.json",
            {
                "schemaVersion": 1,
                "ownerId": record.owner_id,
                "resourceId": record.resource_id,
                "requestId": self.stop_request_id,
                "state": response["state"],
                "message": str(response.get("message", ""))[:512],
            },
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
                "ROCKWELL_RESOURCE_REGISTRY": str(ledger.directory.parent),
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
