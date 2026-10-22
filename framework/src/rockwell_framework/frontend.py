"""Optional stdio frontend admission and pinned parent monitoring; no app startup."""

from __future__ import annotations

import ctypes
import json
import os
import time
from contextlib import ExitStack
from pathlib import Path

import anyio
from universal_tools.windows_resources import ProcessDriver

from .publication import ordinary


def frontend_limit(root):
    path = ordinary(Path(root) / "service.local.json")
    if not path.exists():
        return 4
    with path.open("rb") as stream:
        raw = stream.read(1024 * 1024 + 1)
    if len(raw) > 1024 * 1024:
        raise ValueError("Frontend settings exceed 1 MiB")

    def unique(pairs):
        result = {}
        for key, value in pairs:
            key = key.casefold()
            if key in result:
                raise ValueError("Duplicate frontend setting")
            result[key] = value
        return result

    options = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique)["studio5000"]
    limit = options.get("maximummcpclients", 4)
    if type(limit) is not int or not 1 <= limit <= 16:
        raise ValueError("MaximumMcpClients must be an integer in 1..16")
    return limit


class ExclusiveFile:
    """Same Windows share-none admission protocol as Core.McpClientLease."""

    def __init__(self, path):
        from ctypes import wintypes as w

        path = ordinary(path)
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        create = self.kernel.CreateFileW
        create.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, w.LPVOID, w.DWORD, w.DWORD, w.HANDLE]
        create.restype = w.HANDLE
        self.kernel.CloseHandle.argtypes = [w.HANDLE]
        self.kernel.CloseHandle.restype = w.BOOL
        self.handle = create(str(path), 0xC0000000, 0, None, 4, 0x80, None)
        if self.handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self):
        if self.handle is not None:
            self.kernel.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


def acquire_slot(root, maximum, *, opener=ExclusiveFile):
    if type(maximum) is not int or not 1 <= maximum <= 16:
        raise ValueError("Use 1..16 frontend slots")
    directory = ordinary(Path(root) / ".local/mcp-clients")
    directory.mkdir(parents=True, exist_ok=True)
    admission = None
    for attempt in range(21):
        try:
            admission = opener(directory / "admission.lock")
            break
        except OSError as exc:
            if getattr(exc, "winerror", None) not in {32, 33} or attempt == 20:
                raise
            time.sleep(0.01)
    with admission:
        active, available = 0, None
        for index in range(16):
            try:
                with opener(directory / f"slot-{index}.lock"):
                    if available is None:
                        available = index
            except OSError as exc:
                if getattr(exc, "winerror", None) not in {32, 33}:
                    raise
                active += 1
        if active >= maximum or available is None:
            raise ValueError(
                "mcp_client_limit: close an unused frontend; no existing client stopped"
            )
        return opener(directory / f"slot-{available}.lock")


def parent_snapshot():
    from ctypes import wintypes as w

    class Entry(ctypes.Structure):
        _fields_ = [
            ("size", w.DWORD),
            ("usage", w.DWORD),
            ("pid", w.DWORD),
            ("heap", ctypes.c_size_t),
            ("module", w.DWORD),
            ("threads", w.DWORD),
            ("parent", w.DWORD),
            ("priority", w.LONG),
            ("flags", w.DWORD),
            ("exe", w.WCHAR * 260),
        ]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateToolhelp32Snapshot.argtypes = [w.DWORD, w.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = w.HANDLE
    kernel.CloseHandle.argtypes = [w.HANDLE]
    for name in ("Process32FirstW", "Process32NextW"):
        function = getattr(kernel, name)
        function.argtypes = [w.HANDLE, ctypes.POINTER(Entry)]
        function.restype = w.BOOL
    handle = kernel.CreateToolhelp32Snapshot(2, 0)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        entry = Entry()
        entry.size = ctypes.sizeof(Entry)
        if not kernel.Process32FirstW(handle, ctypes.byref(entry)):
            raise ctypes.WinError(ctypes.get_last_error())
        result = {}
        while True:
            result[entry.pid] = entry.parent
            if not kernel.Process32NextW(handle, ctypes.byref(entry)):
                if ctypes.get_last_error() != 18:
                    raise ctypes.WinError(ctypes.get_last_error())
                return result
    finally:
        kernel.CloseHandle(handle)


def pin_owners(stack, driver, parents, pid):
    child = driver.capture(pid)
    owners = []
    for _ in range(4):
        parent = parents.get(pid)
        if not parent or parent == pid:
            raise ValueError("Cannot establish frontend owner")
        identity = driver.capture(parent)
        if identity["host_id"] != child["host_id"] or int(identity["created"]) > int(
            child["created"]
        ):
            raise ValueError("Frontend parent identity changed")
        handle = stack.enter_context(driver.open(identity))
        if not handle.alive():
            raise ValueError("Frontend parent exited")
        owners.append(handle)
        if Path(identity["executable"]).stem.lower() not in {"cmd", "powershell", "pwsh"}:
            return owners
        pid, child = parent, identity
    raise ValueError("Frontend launcher chain exceeds four owners")


async def serve_owned_stdio(surface, owners):
    # Use the SDK's public stdio lifecycle. Cancellation closes this connection,
    # never the independently serving HTTP app or another frontend.
    async with anyio.create_task_group() as group:

        async def monitor():
            while all(handle.alive() for handle in owners):
                await anyio.sleep(0.1)
            group.cancel_scope.cancel()

        group.start_soon(monitor)
        try:
            await surface.run_stdio_async()
        finally:
            group.cancel_scope.cancel()


def run_frontend(surface, root):
    if os.name != "nt":
        raise ValueError("Use the assessed Windows frontend ownership profile")
    with ExitStack() as stack:
        stack.enter_context(acquire_slot(root, frontend_limit(root)))
        owners = pin_owners(stack, ProcessDriver(), parent_snapshot(), os.getpid())
        anyio.run(serve_owned_stdio, surface, owners)
