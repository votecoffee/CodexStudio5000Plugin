"""Frontend policy/SDK-lifecycle stubs and temporary OS file handles only."""

import json
import os
from contextlib import ExitStack, contextmanager

import anyio
import pytest

from rockwell_framework.frontend import (
    ExclusiveFile,
    acquire_slot,
    frontend_limit,
    pin_owners,
    serve_owned_stdio,
)


def test_limits_reject_duplicates_and_boolean_or_unbounded_values(tmp_path):
    assert frontend_limit(tmp_path) == 4
    path = tmp_path / "service.local.json"
    for value in (True, "4", 0, 17):
        path.write_text(json.dumps({"Studio5000": {"MaximumMcpClients": value}}))
        with pytest.raises(ValueError):
            frontend_limit(tmp_path)
    path.write_text('{"Studio5000":{"MaximumMcpClients":2,"maximummcpclients":3}}')
    with pytest.raises(ValueError, match="Duplicate"):
        frontend_limit(tmp_path)


@pytest.mark.skipif(os.name != "nt", reason="Assessed Windows share-none protocol")
def test_native_slot_handles_count_higher_slots_and_release_after_close(tmp_path):
    with acquire_slot(tmp_path, 2), acquire_slot(tmp_path, 2), pytest.raises(ValueError, match="mcp_client_limit"):
        acquire_slot(tmp_path, 2)
    directory = tmp_path / ".local/mcp-clients"
    with ExclusiveFile(directory / "slot-15.lock"), pytest.raises(ValueError, match="mcp_client_limit"):
        acquire_slot(tmp_path, 1)
    with acquire_slot(tmp_path, 1):
        assert len(list(directory.iterdir())) == 17


class Driver:
    def __init__(self):
        self.closed = []
        self.items = {
            3: {"host_id": "a" * 64, "created": "300", "executable": "C:/frontend.exe"},
            2: {"host_id": "a" * 64, "created": "200", "executable": "C:/cmd.exe"},
            1: {"host_id": "a" * 64, "created": "100", "executable": "C:/codex.exe"},
        }

    def capture(self, pid):
        return {**self.items[pid], "pid": pid}

    @contextmanager
    def open(self, identity):
        class Handle:
            def alive(self):
                return True

        try:
            yield Handle()
        finally:
            self.closed.append(identity["pid"])


def test_owner_chain_pins_wrappers_and_rejects_reused_or_foreign_parents():
    driver = Driver()
    with ExitStack() as stack:
        assert len(pin_owners(stack, driver, {3: 2, 2: 1}, 3)) == 2
        assert driver.closed == []
    assert driver.closed == [1, 2]
    driver.items[2]["created"] = "301"
    with ExitStack() as stack, pytest.raises(ValueError, match="identity changed"):
        pin_owners(stack, driver, {3: 2}, 3)
    driver.items[2]["created"] = "200"
    driver.items[2]["host_id"] = "b" * 64
    with ExitStack() as stack, pytest.raises(ValueError, match="identity changed"):
        pin_owners(stack, driver, {3: 2}, 3)


@pytest.mark.parametrize("eof", [True, False])
def test_sdk_eof_or_parent_loss_closes_only_the_stub_stdio_surface(eof):
    class Parent:
        def alive(self):
            return eof

    class Surface:
        closed = False

        async def run_stdio_async(self):
            try:
                if not eof:
                    await anyio.sleep_forever()
            finally:
                self.closed = True

    async def run():
        surface = Surface()
        with anyio.fail_after(2):
            await serve_owned_stdio(surface, [Parent()])
        assert surface.closed

    anyio.run(run)
