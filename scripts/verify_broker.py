"""Exercise the real broker protocol with an isolated offline Rockwell profile.

Run with the broker's development Python (mcp/jsonschema dependencies installed).
Neither real Codex settings nor normal app/broker registrations are changed.
"""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--broker-root", required=True, type=Path)
    parser.add_argument("--workspace-root", required=True, type=Path,
                        help="Profile produced by Verify-Isolated.ps1, never the normal checkout")
    parser.add_argument("--standalone-launcher", type=Path, help="Optional installed package scripts/Start-Mcp.ps1 to verify against the same isolated service")
    args = parser.parse_args()
    root, broker = args.workspace_root.resolve(), args.broker_root.resolve()
    config = json.loads((root / "service.local.json").read_text(encoding="utf-8-sig"))
    options = config["Studio5000"]
    if ("isolated-validation" not in root.parts or options.get("EnableControllerOperations")
            or options.get("ControllerTargets") or options.get("NativeBridgePath")
            or options.get("CipBridgePath")):
        raise ValueError("Use an isolated offline validation profile with all native/target access disabled")
    sys.path.insert(0, str(broker / "src"))
    os.environ["PYTHONPATH"] = str(broker / "src")
    from universal_tools.cli import initialize, register, stop
    from universal_tools.models import atomic_json
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    import jsonschema

    output = root / "artifacts" / "broker-compatibility" / uuid.uuid4().hex
    output.mkdir(parents=True)
    state = output / "broker-state"
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    initialize(state, port)
    # A new loopback port prevents accidentally testing another already running
    # service if someone selects an in-use validation profile. Its state lock
    # makes our child fail before this fresh endpoint can become healthy.
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        app_port = sock.getsockname()[1]
    config["ServiceUrl"] = f"http://127.0.0.1:{app_port}"
    atomic_json(root / "service.local.json", config)
    if args.standalone_launcher:
        pointer = json.loads((args.standalone_launcher.resolve().parent.parent / "runtime.json").read_text(encoding="utf-8-sig"))
        if Path(pointer["root"]).resolve() != root:
            raise ValueError("Standalone package must point at this isolated profile, never the normal app")
    descriptor = {
        "schema_version": 1, "id": "rockwell-mcp-server", "display_name": "Offline Rockwell fixture",
        "transport": {"type": "stdio", "command": str(Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"), "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(root / "scripts/Start-Mcp.ps1")], "cwd": str(root)},
        "enabled": True, "allow_writes": True,
        "allowed_write_tools": ["studio_register_project", "studio_preview_changes", "studio_apply_changes"],
        "session_mode": "shared", "resource_id": "isolated-rockwell", "timeout_seconds": 60,
        "max_argument_bytes": 8388608,
        "capability_catalog": {"schema_version": 1, "tool": "rockwell_broker_capabilities", "arguments": {}},
    }
    manifest = output / "fixture.service.json"
    atomic_json(manifest, descriptor)
    register(state, manifest)
    checks = []

    def check(condition, description):
        if not condition:
            raise AssertionError(description)
        checks.append(description)
        print("PASS:", description, flush=True)

    def data(result):
        if result.isError:
            raise AssertionError(result.model_dump_json())
        return result.structuredContent or json.loads(result.content[0].text)

    async def run():
        if args.standalone_launcher:
            direct = StdioServerParameters(command=descriptor["transport"]["command"], args=["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(args.standalone_launcher.resolve())], env=dict(os.environ))
            async with stdio_client(direct) as (reader, writer):
                async with ClientSession(reader, writer) as session:
                    await session.initialize()
                    check(len((await session.list_tools()).tools) == 59, "Installed standalone package launcher exposes all 59 app tools without a broker")
                    catalog = data(await session.call_tool("rockwell_broker_capabilities", {}))
                    check(catalog["schema_version"] == 1, "Installed standalone launcher reaches the same shared-service discovery metadata")
        params = StdioServerParameters(command=sys.executable, args=["-m", "universal_tools", "--state", str(state), "proxy"], env=dict(os.environ))
        async with stdio_client(params) as (reader, writer):
            async with ClientSession(reader, writer) as session:
                await session.initialize()
                check(len((await session.list_tools()).tools) == 14, "Universal Tools retains its fourteen-tool frontend with no Rockwell plugin installed")
                rows = data(await session.call_tool("refresh_services", {}))["services"]
                row = next(r for r in rows if r["service_id"] == "rockwell-mcp-server")
                check(row["status"] == "available", "Real broker discovers the app-owned stdio adapter and normalized catalog")

                async def invoke(tool, arguments, write=False, request_id=None):
                    tool_id = "rockwell-mcp-server/" + tool
                    definition = data(await session.call_tool("describe_tool", {"tool_id": tool_id}))
                    payload = {"tool_id": tool_id, "catalog_revision": definition["catalog_revision"], "arguments": arguments}
                    if write:
                        payload["request_id"] = request_id or uuid.uuid4().hex
                    return await session.call_tool("call_write_tool" if write else "call_read_tool", payload)

                matches = data(await session.call_tool("search_tools", {"query": "workflow", "kind": "capability", "limit": 100}))["matches"]
                guides = [m for m in matches if m["capability_id"].startswith("workflow.")]
                check(len(guides) == 3 and all(m["callable"] is False for m in guides), "All standalone workflow guides are searchable discovery-only contributions")
                workflow_schema = json.loads((broker / "examples/workflow.schema.json").read_text(encoding="utf-8"))
                for match in guides:
                    capability = data(await session.call_tool("describe_capability", {"service_id": match["service_id"], "capability_id": match["capability_id"], "capability_revision": match["capability_revision"]}))
                    discovery = capability["discovery"]
                    guide = data(await invoke(discovery["tool_id"].split("/", 1)[1], discovery["arguments"]))
                    jsonschema.Draft202012Validator(workflow_schema).validate(guide)
                    check(hashlib.sha256(guide["instructions"]["text"].encode()).hexdigest() == guide["instructions"]["sha256"], "Guide schema and exact UTF-8 digest pass through the broker: " + guide["id"])
                    for reference in guide["references"]:
                        result = data(await invoke(reference["read"]["tool"], reference["read"]["arguments"]))
                        check(hashlib.sha256(result["content"].encode()).hexdigest() == reference["sha256"], "Named workflow reference retains its pinned hash through the broker")
                    stale = dict(discovery["arguments"], expectedRevision="stale")
                    check((await invoke("rockwell_read_workflow", stale)).isError, "Stale guide revisions fail through the broker: " + guide["id"])

                operation = data(await session.call_tool("search_tools", {"query": "studio_transaction_preview", "kind": "capability"}))["matches"]
                match = next(m for m in operation if m["capability_id"] == "logix.studio_transaction_preview")
                detail = data(await session.call_tool("describe_capability", {"service_id": match["service_id"], "capability_id": match["capability_id"], "capability_revision": match["capability_revision"]}))
                described = data(await invoke("studio_extensions_describe", detail["discovery"]["arguments"]))
                check(described["name"] == "studio_transaction_preview" and described["effect"] == "read", "Broker capability discovery reaches the exact generation-bound inner operation schema")
                source = root / "examples/Demo.L5X"
                original = hashlib.sha256(source.read_bytes()).hexdigest()
                project = data(await invoke("studio_register_project", {"path": str(source)}, write=True))
                preview_args = {"projectId": project["id"], "expectedSha256": project["currentSha256"], "edits": [{"operation": "set_text", "xPath": "//Tag[@Name='CycleCount']/Description", "value": "Generated broker validation"}]}
                check((await invoke("studio_preview_changes", preview_args)).isError, "Broker read channel rejects a preview with local staging effects")
                preview = data(await invoke("studio_preview_changes", preview_args, write=True))
                apply = {"projectId": project["id"], "previewId": preview["id"], "expectedSha256": preview["baseSha256"]}
                bad = dict(apply, expectedSha256="stale")
                check((await invoke("studio_apply_changes", bad, write=True)).isError, "App source/review guards remain effective through broker write routing")
                updated = data(await invoke("studio_apply_changes", apply, write=True))
                check(updated["revision"] == 1 and updated["currentSha256"] != project["currentSha256"], "Authorized fixture preview/apply creates exactly one offline revision through the broker")
                repeated = data(await invoke("studio_apply_changes", apply, write=True))
                check(repeated["revision"] == updated["revision"] and repeated["currentSha256"] == updated["currentSha256"], "App idempotent apply receipts prevent a duplicate offline revision through another broker request")
                check((await invoke("studio_extensions_control", {"call": {"extensionId": "logix", "operation": "studio_sdk_start_job", "generation": "unselected", "arguments": {}}}, write=True)).isError, "Exact outer-tool allowlist blocks control operations before app dispatch")
                check(hashlib.sha256(source.read_bytes()).hexdigest() == original, "Original generated PLC source remains unchanged")

    env = dict(os.environ, STUDIO5000_PLUGIN_ROOT=str(root))
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    with (output / "service.log").open("w", encoding="utf-8") as log:
        child = subprocess.Popen([str(root / "artifacts/app/Service/Studio5000.Service.exe")], cwd=root, env=env, stdout=log, stderr=log, creationflags=flags)
        try:
            for _ in range(200):
                if child.poll() is not None:
                    raise RuntimeError("Isolated service exited; inspect its log (an existing profile may be running)")
                try:
                    with urllib.request.urlopen(config["ServiceUrl"] + "/health", timeout=0.5):
                        break
                except OSError:
                    time.sleep(0.1)
            else:
                raise TimeoutError("Isolated service startup timed out")
            asyncio.run(run())
            atomic_json(output / "summary.json", {"passed": len(checks), "checks": checks, "native_connection_attempted": False, "real_codex_modified": False})
            print(f"{len(checks)} real broker protocol checks passed: {output}")
        finally:
            try:
                stop(state)
            finally:
                child.terminate()
                child.wait(timeout=15)


if __name__ == "__main__":
    main()
