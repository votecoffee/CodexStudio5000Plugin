"""Installed-wheel framework profile against the actual .NET adapter, offline only."""

from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import os
import shutil
import socket
import subprocess
import sys
import time
import urllib.request
import uuid
from contextlib import AsyncExitStack
from pathlib import Path

from mcp import ClientSession, StdioServerParameters, types
from mcp.client.stdio import stdio_client
from rockwell_framework.publication import (
    APP_ID,
    baseline,
    check_versions,
    publish,
    verify,
)
from universal_tools.models import atomic_json, load_settings


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def data(result):
    if result.is_error:
        raise AssertionError(result.model_dump_json(by_alias=True))
    return result.structured_content or json.loads(result.content[0].text)


def wire(value, *, normalize_success=True):
    result = value.model_dump(by_alias=True, mode="json", exclude_unset=True)
    # The C# SDK omits the optional false flag; the Python server emits it.
    # Preserve all received fields, normalizing only that documented default.
    if normalize_success:
        result.setdefault("isError", False)
    return result


async def tools(session):
    cursor, result = None, {}
    for _ in range(100):
        page = await session.list_tools(
            params=types.PaginatedRequestParams(cursor=cursor)
        )
        for tool in page.tools:
            if tool.name in result:
                raise AssertionError("Duplicate tool in paginated list")
            result[tool.name] = tool.model_dump(
                by_alias=True, mode="json", exclude_none=True
            )
        cursor = page.next_cursor
        if cursor is None:
            return result
    raise AssertionError("Tool pagination did not terminate")


def wait_ready(child, url, *, token=None):
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        if child.poll() is not None:
            raise RuntimeError("Owned fixture exited; inspect its local log")
        try:
            headers = {"Authorization": "Bearer " + token} if token else {}
            with urllib.request.urlopen(
                urllib.request.Request(url, headers=headers), timeout=1
            ) as response:
                if response.status == 200:
                    return
        except OSError:
            time.sleep(0.1)
    raise TimeoutError("Owned fixture did not become ready")


async def exercise(root, state, output, check):
    params = StdioServerParameters(
        command=sys.executable,
        args=["-m", "rockwell_framework", "--state", str(state), "frontend"],
        env=dict(os.environ),
    )
    direct_params = StdioServerParameters(
        command=str(root / "artifacts/app/Mcp/Studio5000.Mcp.exe"),
        env=dict(os.environ, STUDIO5000_PLUGIN_ROOT=str(root)),
    )
    async with AsyncExitStack() as stack:

        async def connect(parameters):
            reader, writer = await stack.enter_async_context(stdio_client(parameters))
            session = await stack.enter_async_context(ClientSession(reader, writer))
            init = await session.initialize()
            check(
                init.protocol_version == "2025-11-25",
                "Actual adapter/frontend negotiates the assessed legacy protocol",
            )
            return session

        direct, first, second = (
            await connect(direct_params),
            await connect(params),
            await connect(params),
        )
        frontend_definitions = await tools(first)
        backend_definitions = await tools(direct)
        check(
            len(frontend_definitions) == 14 and len(backend_definitions) == 59,
            "Standalone .NET keeps 59 tools; the optional app-owned host has fourteen fixed tools",
        )
        check(
            frontend_definitions == await tools(second),
            "Two distinct frontends receive identical native definitions",
        )
        discovery = data(await first.call_tool("refresh_services", {}))
        row = next(
            (r for r in discovery["services"] if r["service_id"] == APP_ID), None
        )
        check(
            row is not None and row["status"] == "available",
            "Actual C# backend discovery: " + json.dumps(discovery),
        )
        description = data(
            await first.call_tool("describe_service", {"service_id": APP_ID})
        )
        check(
            description["session_mode"] == "transient"
            and description["initialization"]["protocolVersion"] == "2025-11-25",
            "Independent service state uses explicit targets and transient backend adapters",
        )
        argument_budgets = []
        for name, expected in backend_definitions.items():
            actual = data(
                await first.call_tool("describe_tool", {"tool_id": APP_ID + "/" + name})
            )
            check(
                actual["definition"] == expected,
                "Complete actual C# tool definition is retained: " + name,
            )
            argument_budgets.append(
                actual["max_argument_bytes"] == 8388608
                and actual["max_argument_depth"] == 64
                and actual["max_argument_nodes"] == 100000
            )
        check(
            all(argument_budgets),
            "Framework advertises pinned byte and structural budgets for every app tool",
        )

        async def invoke(
            session, name, arguments, *, write=False, request_id=None, described=None
        ):
            described = described or data(
                await session.call_tool(
                    "describe_tool", {"tool_id": APP_ID + "/" + name}
                )
            )
            payload = {
                "tool_id": APP_ID + "/" + name,
                "catalog_revision": described["catalog_revision"],
                "arguments": arguments,
            }
            if write:
                payload["request_id"] = request_id or uuid.uuid4().hex
            return await session.call_tool(
                "call_write_tool" if write else "call_read_tool", payload
            )

        for name in (
            "studio_capabilities",
            "studio_list_projects",
            "rockwell_broker_capabilities",
        ):
            direct_result = await direct.call_tool(name, {})
            framework_result = await invoke(first, name, {})
            reference, routed = wire(direct_result), wire(framework_result)
            atomic_json(
                output / (name + ".direct.json"),
                wire(direct_result, normalize_success=False),
            )
            atomic_json(
                output / (name + ".framework.json"),
                wire(framework_result, normalize_success=False),
            )
            check(
                reference == routed,
                "Complete direct/framework result fields match: " + name,
            )
        found = data(
            await first.call_tool(
                "search_tools",
                {"query": "workflow", "kind": "capability", "limit": 100},
            )
        )["matches"]
        guides = [m for m in found if m["capability_id"].startswith("workflow.")]
        check(
            len(guides) == 3 and all(m["callable"] is False for m in guides),
            "Standalone workflows remain discovery-only hints through the app host",
        )
        for guide in guides:
            detail = data(
                await first.call_tool(
                    "describe_capability",
                    {
                        k: guide[k]
                        for k in ("service_id", "capability_id", "capability_revision")
                    },
                )
            )
            query = detail["discovery"]
            name = query["tool_id"].split("/", 1)[1]
            check(
                wire(await direct.call_tool(name, query["arguments"]))
                == wire(await invoke(first, name, query["arguments"])),
                "Complete standalone workflow content matches its framework route: "
                + guide["capability_id"],
            )

        # A new complete deployment is a new publication, while both MCP clients stay connected.
        previous = data(
            await first.call_tool(
                "describe_tool", {"tool_id": APP_ID + "/studio_capabilities"}
            )
        )
        candidate = output / "generated next backend"
        shutil.copytree(root / "artifacts/app/Mcp", candidate)
        (candidate / "publication-fixture.txt").write_text(
            "Generated deployment identity change; no native code changes.",
            encoding="utf-8",
        )
        allowed = [
            "studio_register_project",
            "studio_preview_changes",
            "studio_apply_changes",
        ]
        publish(root, state, backend_root=candidate, allowed_write_tools=allowed)
        refreshed = data(await second.call_tool("refresh_services", {}))
        check(
            refreshed["backend_publications"][0]["status"] == "applied",
            "A second connected frontend explicitly refreshes the new immutable backend",
        )
        check(
            await tools(first) == frontend_definitions
            and await tools(second) == frontend_definitions,
            "Backend publication preserves every frontend definition and both retained connections",
        )
        check(
            (
                await invoke(first, "studio_capabilities", {}, described=previous)
            ).is_error,
            "Previous backend catalog revisions cannot dispatch after publication",
        )
        check(
            wire(await direct.call_tool("studio_capabilities", {}))
            == wire(await invoke(first, "studio_capabilities", {})),
            "The first connected frontend reads the replacement backend without reconnecting",
        )

        source = root / "examples/Demo.L5X"
        original = hashlib.sha256(source.read_bytes()).hexdigest()
        project = data(
            await invoke(
                first, "studio_register_project", {"path": str(source)}, write=True
            )
        )
        inspect = {"projectId": project["id"], "kind": "Tag", "limit": 20}
        check(
            wire(await direct.call_tool("studio_inspect_project", inspect))
            == wire(await invoke(second, "studio_inspect_project", inspect)),
            "A second frontend reads the exact explicit project, without implicit chat selection",
        )
        preview_args = {
            "projectId": project["id"],
            "expectedSha256": project["currentSha256"],
            "edits": [
                {
                    "operation": "set_text",
                    "xPath": "//Tag[@Name='CycleCount']/Description",
                    "value": "Framework fixture café 深 μ",
                }
            ],
        }
        check(
            (await invoke(first, "studio_preview_changes", preview_args)).is_error,
            "Read dispatch cannot stage a project mutation",
        )
        preview = data(
            await invoke(first, "studio_preview_changes", preview_args, write=True)
        )
        apply = {
            "projectId": project["id"],
            "previewId": preview["id"],
            "expectedSha256": preview["baseSha256"],
        }
        request_id = uuid.uuid4().hex
        updated = data(
            await invoke(
                first, "studio_apply_changes", apply, write=True, request_id=request_id
            )
        )
        check(
            updated["revision"] == project["revision"] + 1,
            "Actual Core preview/apply records exactly one offline revision",
        )
        check(
            (
                await invoke(
                    second,
                    "studio_apply_changes",
                    apply,
                    write=True,
                    request_id=request_id,
                )
            ).is_error,
            "Another frontend cannot dispatch a claimed write request ID again",
        )
        receipt = data(
            await second.call_tool("get_request_status", {"request_id": request_id})
        )
        check(
            receipt["status"] == "completed",
            "Completed write receipt remains available to inspect without replay",
        )
        repeated = data(await invoke(second, "studio_apply_changes", apply, write=True))
        check(
            repeated["revision"] == updated["revision"]
            and repeated["currentSha256"] == updated["currentSha256"],
            "A deliberately repeated completed apply retains Core's durable no-duplicate receipt",
        )
        bad = dict(apply, expectedSha256="stale")
        check(
            (await invoke(first, "studio_apply_changes", bad, write=True)).is_error,
            "Core target/review hash rejection survives the new framework boundary",
        )
        check(
            (
                await invoke(
                    first,
                    "studio_extensions_control",
                    {
                        "call": {
                            "extensionId": "logix",
                            "operation": "studio_sdk_start_job",
                            "generation": "unselected",
                            "arguments": {},
                        }
                    },
                    write=True,
                )
            ).is_error,
            "Exact write allowlists deny native control before adapter dispatch",
        )
        check(
            hashlib.sha256(source.read_bytes()).hexdigest() == original,
            "Original generated PLC source remains byte-identical",
        )
        check(
            wire(await direct.call_tool("studio_inspect_project", inspect))
            == wire(await invoke(second, "studio_inspect_project", inspect)),
            "Final project state matches through direct and framework routes after negative cases",
        )
        # An app error is not assumed to mean zero effects by the generic framework.
        (candidate / "publication-fixture.txt").write_text(
            "Another generated deployment identity.", encoding="utf-8"
        )
        publish(root, state, backend_root=candidate, allowed_write_tools=allowed)
        blocked = data(await second.call_tool("refresh_services", {}))
        check(
            blocked["backend_publications"][0]["status"] == "deferred",
            "An app-error write retains the framework's explicit reconciliation barrier",
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace-root", required=True, type=Path)
    args = parser.parse_args()
    check_versions()
    root = args.workspace_root.resolve()
    config = json.loads((root / "service.local.json").read_text("utf-8-sig"))
    options = config["Studio5000"]
    if (
        "isolated-validation" not in root.parts
        or options.get("EnableControllerOperations")
        or options.get("ControllerTargets")
        or options.get("NativeBridgePath")
        or options.get("CipBridgePath")
    ):
        raise ValueError(
            "Use an isolated profile with all native and controller access disabled"
        )
    output = root / "artifacts/framework-compatibility" / uuid.uuid4().hex
    output.mkdir(parents=True)
    # Native Windows process launch needs a short deployment path. Keep this owned
    # fixture outside the deeply nested report tree and retain it for diagnostics.
    state = Path(__file__).resolve().parents[1] / ".local/fwk" / uuid.uuid4().hex[:16]
    config["ServiceUrl"] = f"http://127.0.0.1:{free_port()}"
    atomic_json(root / "service.local.json", config)
    publish(
        root,
        state,
        port=free_port(),
        allowed_write_tools=[
            "studio_register_project",
            "studio_preview_changes",
            "studio_apply_changes",
        ],
    )
    checks, children = [], []

    def check(condition, description):
        if not condition:
            raise AssertionError(description)
        checks.append(description)
        print("PASS:", description, flush=True)

    try:
        for label, command, env in (
            (
                "service",
                [str(root / "artifacts/app/Service/Studio5000.Service.exe")],
                dict(os.environ, STUDIO5000_PLUGIN_ROOT=str(root)),
            ),
            (
                "framework",
                [
                    sys.executable,
                    "-m",
                    "rockwell_framework",
                    "--state",
                    str(state),
                    "serve",
                ],
                dict(os.environ),
            ),
        ):
            with (output / (label + ".log")).open("w", encoding="utf-8") as log:
                child = subprocess.Popen(
                    command,
                    cwd=root,
                    env=env,
                    stdout=log,
                    stderr=log,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
            children.append(child)
            settings = load_settings(state)
            wait_ready(
                child,
                config["ServiceUrl"] + "/health"
                if label == "service"
                else settings.url + "/health",
                token=settings.token if label == "framework" else None,
            )
        asyncio.run(exercise(root, state, output, check))
        verify(state)
        atomic_json(
            output / "summary.json",
            {
                "passed": len(checks),
                "checks": checks,
                "baseline": baseline(),
                "native_connection_attempted": False,
                "installed_runtime_modified": False,
                "framework_module": __import__("rockwell_framework").__file__,
            },
        )
        print(f"{len(checks)} installed-wheel framework checks passed: {output}")
    finally:
        for child in reversed(children):
            if child.poll() is None:
                child.terminate()
                child.wait(timeout=15)


if __name__ == "__main__":
    main()
