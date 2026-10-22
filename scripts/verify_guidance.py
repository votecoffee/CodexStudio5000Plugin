"""Installed app wheel, actual shared Core guidance and both explicit Skills endpoints; offline only."""

from __future__ import annotations

import argparse
import asyncio
import base64
import json
import os
import subprocess
import sys
import uuid
from contextlib import asynccontextmanager
from pathlib import Path

import httpx2
from mcp import Client, StdioServerParameters
from mcp.client.streamable_http import streamable_http_client
from mcp.types import PaginatedRequestParams, Request
from rockwell_framework.guidance import GUIDES, GuidanceSource, checksum
from rockwell_framework.publication import check_versions, publish
from universal_tools.models import atomic_json, load_settings
from universal_tools.skill_catalog import SkillGet, SkillList, SkillParams
from universal_tools.skill_client import SkillClient
from verify_framework import data, free_port, wait_ready


@asynccontextmanager
async def connection(settings):
    @asynccontextmanager
    async def transport():
        headers = {
            "Authorization": "Bearer " + settings.token,
            "x-universal-tools-client": uuid.uuid4().hex,
        }
        async with (
            httpx2.AsyncClient(headers=headers, trust_env=False) as http,
            streamable_http_client(settings.url + "/mcp", http_client=http) as streams,
        ):
            yield streams

    async with Client(
        transport(), mode="2026-07-28", extensions=[SkillClient()], cache=None
    ) as client:
        yield client


async def exercise(root, state, check):
    fetch = GuidanceSource(root)
    metadata = fetch("/api/broker/capabilities")
    doctor_entry = next(entry for entry in metadata["capabilities"] if entry["id"] == "protocol.features")
    direct_doctor = fetch("/api/broker/workflow", doctor_entry["discovery"]["arguments"])
    check(
        direct_doctor["profile"] == "standalone-dotnet",
        "Direct route reports its actual protocol gaps",
    )
    expected = {}
    for entry in metadata["capabilities"]:
        if not entry["id"].startswith("workflow."):
            continue
        args = entry["discovery"]["arguments"]
        guide = fetch("/api/broker/workflow", args)
        name = guide["id"]
        expected["skill://" + name + "/SKILL.md"] = guide["skill_source"][
            "text"
        ].encode()
        for reference in guide["references"]:
            result = fetch("/api/broker/reference", reference["read"]["arguments"])
            expected["skill://" + name + "/references/" + reference["id"] + ".md"] = (
                result["content"].encode()
            )
    check(
        len(expected) == 10,
        "Actual shared Core publishes three canonical guides and seven references",
    )

    async def inspect(client, label):
        check(
            client.protocol_version == "2026-07-28",
            label + " negotiates the modern profile",
        )
        catalog = await client.session.send_request(
            Request(method="skills/list", params=PaginatedRequestParams()), SkillList
        )
        check(
            {entry["frontmatter"]["name"] for entry in catalog.skills} == GUIDES,
            label + " publishes all three original names",
        )
        read_count = 0
        for entry in catalog.skills:
            result = await client.session.send_request(
                Request(method="skills/get", params=SkillParams(uri=entry["uri"])),
                SkillGet,
            )
            assert result.skill == entry
            for resource in entry["resources"]:
                content = (await client.read_resource(resource["uri"])).contents[0]
                raw = (
                    content.text.encode()
                    if hasattr(content, "text")
                    else base64.b64decode(content.blob)
                )
                assert raw == expected[resource["uri"]]
                assert len(raw) == resource["size"]
                assert "sha256:" + checksum(raw) == resource["digest"]
                read_count += 1
        check(
            read_count == 10,
            label
            + " list/get/read hashes and bytes match the authoritative Core readers",
        )
        tools = {tool.name for tool in (await client.list_tools()).tools}
        check(
            len(tools) == 16
            and "rockwell_refresh_guidance" in tools
            and "rockwell_protocol_doctor" in tools,
            label
            + " preserves fourteen ordinary tools and adds two explicit metadata tools",
        )
        doctor = data(await client.call_tool("rockwell_protocol_doctor", {}))
        revision = doctor["skills"]["revision"]
        result = data(
            await client.call_tool(
                "rockwell_refresh_guidance", {"expectedRevision": revision}
            )
        )
        check(
            not result["changed"] and result["revision"] == revision,
            label + " explicitly refreshes unchanged content on the same connection",
        )
        stale = await client.call_tool(
            "rockwell_refresh_guidance", {"expectedRevision": "stale"}
        )
        check(
            stale.is_error,
            label + " rejects a stale publication without replacing the catalog",
        )

    settings = load_settings(state)
    async with connection(settings) as client:
        await inspect(client, "Authenticated HTTP")
    params = StdioServerParameters(
        command=sys.executable,
        args=[
            "-m",
            "rockwell_framework",
            "--state",
            str(state),
            "frontend",
            "--skills",
        ],
        env=dict(os.environ),
    )
    async with Client(
        params, mode="2026-07-28", extensions=[SkillClient()], cache=None
    ) as client:
        await inspect(client, "Explicit stdio frontend")
    async with httpx2.AsyncClient(trust_env=False) as http:
        check(
            (await http.get(settings.url + "/health")).status_code == 401,
            "HTTP metadata transport rejects an unauthenticated request",
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace-root", type=Path, required=True)
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
            "Use an isolated controller-disabled fixture without native bridges"
        )
    output = root / "artifacts/guidance-compatibility" / uuid.uuid4().hex
    output.mkdir(parents=True)
    state = Path(__file__).resolve().parents[1] / ".local/fwk" / uuid.uuid4().hex[:16]
    config["ServiceUrl"] = f"http://127.0.0.1:{free_port()}"
    atomic_json(root / "service.local.json", config)
    publish(root, state, port=free_port())
    checks, children = [], []

    def check(condition, label):
        if not condition:
            raise AssertionError(label)
        checks.append(label)
        print("PASS:", label, flush=True)

    try:
        for label, command in (
            ("service", [str(root / "artifacts/app/Service/Studio5000.Service.exe")]),
            (
                "skills",
                [
                    sys.executable,
                    "-m",
                    "rockwell_framework",
                    "--state",
                    str(state),
                    "serve",
                    "--skills",
                ],
            ),
        ):
            with (output / (label + ".log")).open("wb") as log:
                child = subprocess.Popen(
                    command,
                    cwd=root,
                    env=dict(os.environ, STUDIO5000_PLUGIN_ROOT=str(root)),
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
                token=None if label == "service" else settings.token,
            )
        asyncio.run(exercise(root, state, check))
        atomic_json(
            output / "summary.json",
            {
                "passed": len(checks),
                "checks": checks,
                "native_connection_attempted": False,
                "production_modified": False,
            },
        )
        print(f"{len(checks)} installed-wheel guidance checks passed: {output}")
    finally:
        for child in reversed(children):
            if child.poll() is None:
                child.terminate()
                child.wait(timeout=15)


if __name__ == "__main__":
    main()
