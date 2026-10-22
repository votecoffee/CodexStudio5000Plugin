"""Canonical-source publication and already-connected protocol checks; generated metadata only."""

import asyncio
import base64
import json
import time
from pathlib import Path

import httpx
import pytest
from mcp import Client
from mcp.shared.exceptions import MCPError
from mcp.shared.subscriptions import ResourcesListChanged, ResourceUpdated
from mcp.types import PaginatedRequestParams, Request
from universal_tools.extensions import ExtensionServer
from universal_tools.models import BrokerError
from universal_tools.skill_catalog import SkillGet, SkillList, SkillParams
from universal_tools.skill_client import SkillClient

from rockwell_framework.guidance import (
    GUIDES,
    AppGuidanceCatalog,
    GuidanceSource,
    checksum,
    fetch_snapshot,
    workflow_keys,
)

pytestmark = pytest.mark.anyio


@pytest.fixture
def anyio_backend():
    return "asyncio"


class Source:
    def __init__(self):
        self.files = {}
        for name in sorted(GUIDES):
            self.files[name] = {
                "SKILL.md": f"---\nname: {name}\ndescription: Generated {name} fixture\n---\nRead references/a.md\n",
                "references/a.md": "Generated reference\r\n",
            }
        self.calls = []
        self.corrupt = False
        self.legacy = False
        self.delay = 0

    def __call__(self, path, params=None):
        self.calls.append(path)
        if self.delay:
            time.sleep(self.delay)
        revisions = {
            name: checksum(json.dumps(files, sort_keys=True).encode())
            for name, files in self.files.items()
        }
        if path.endswith("capabilities"):
            return {
                "schema_version": 1,
                "capabilities": [
                    {
                        "id": "workflow." + name,
                        "discovery": {
                            "tool": "rockwell_read_workflow",
                            "arguments": {"workflowId": name, "expectedRevision": revision},
                        },
                    }
                    for name, revision in revisions.items()
                ],
            }
        name = params["workflowId"]
        assert params["expectedRevision"] == revisions[name]
        files = self.files[name]
        if path.endswith("workflow"):
            text = files["SKILL.md"]
            return {
                "id": name,
                "revision": revisions[name],
                **(
                    {}
                    if self.legacy
                    else {"skill_source": {"text": text, "sha256": checksum(text.encode())}}
                ),
                "references": [
                    {
                        "id": key.removeprefix("references/").removesuffix(".md"),
                        "sha256": checksum(value.encode()),
                    }
                    for key, value in files.items()
                    if key != "SKILL.md"
                ],
            }
        text = files["references/" + params["referenceId"] + ".md"]
        sha = checksum(text.encode())
        assert params["expectedSha256"] == sha
        return {
            "id": params["referenceId"],
            "revision": revisions[name],
            "sha256": sha,
            "content": "corrupt" if self.corrupt else text,
        }


def bytes_of(result):
    content = result.contents[0]
    return content.text.encode() if hasattr(content, "text") else base64.b64decode(content.blob)


async def listing(client, cursor=None):
    return await client.session.send_request(
        Request(method="skills/list", params=PaginatedRequestParams(cursor=cursor)), SkillList
    )


async def test_same_connected_client_gets_exact_manifests_and_updated_added_removed_resources():
    source = Source()
    catalog = AppGuidanceCatalog(source, page_size=1)
    server = ExtensionServer(
        "Generated Rockwell metadata", extensions=[catalog], resource_providers=[catalog]
    )
    async with Client(server, mode="2026-07-28", extensions=[SkillClient()], cache=None) as client:
        first = await listing(client)
        old_cursor = first.next_cursor
        assert old_cursor
        entry = first.skills[0]
        get = await client.session.send_request(
            Request(method="skills/get", params=SkillParams(uri=entry["uri"])), SkillGet
        )
        assert get.skill == entry
        for resource in entry["resources"]:
            raw = bytes_of(await client.read_resource(resource["uri"]))
            assert len(raw) == resource["size"] and "sha256:" + checksum(raw) == resource["digest"]
        doctor = (await client.call_tool("rockwell_protocol_doctor", {})).structured_content
        revision = doctor["skills"]["revision"]
        source.files["rockwell-engineering"]["references/a.md"] = "Changed\r\n"
        del source.files["factorytalk-studio"]
        changes = []

        async def changed():
            changes.append(catalog.revision)

        catalog.listeners.add(changed)
        async with client.listen(
            resources_list_changed=True, resource_subscriptions=["broker://skills"]
        ) as events:
            refreshed = await client.call_tool(
                "rockwell_refresh_guidance", {"expectedRevision": revision}
            )
            async with asyncio.timeout(3):
                delivered = [await anext(events), await anext(events)]
            assert any(isinstance(event, ResourcesListChanged) for event in delivered)
            assert any(isinstance(event, ResourceUpdated) for event in delivered)
        assert not refreshed.is_error and refreshed.structured_content["changed"]
        assert len(changes) == 1
        assert (
            bytes_of(await client.read_resource("skill://rockwell-engineering/references/a.md"))
            == b"Changed\r\n"
        )
        with pytest.raises(MCPError):
            await client.read_resource("skill://factorytalk-studio/SKILL.md")
        with pytest.raises(MCPError, match="cursor"):
            await listing(client, old_cursor)
        stale = await client.call_tool("rockwell_refresh_guidance", {"expectedRevision": revision})
        assert stale.is_error
        source.files["factorytalk-studio"] = {
            "SKILL.md": "---\nname: factorytalk-studio\ndescription: Added again\n---\nCurrent fixture\n"
        }
        await catalog.refresh(catalog.revision)
        resources = await client.list_resources()
        assert "skill://factorytalk-studio/SKILL.md" in {str(r.uri) for r in resources.resources}
        new_first = await listing(client)
        for resource in new_first.skills[0]["resources"]:
            assert (
                "sha256:" + checksum(bytes_of(await client.read_resource(resource["uri"])))
                == resource["digest"]
            )
        # The optional directory method is deliberately not advertised/configured.
        assert not catalog.directory_read
    # Events have no replay. A reconnected client explicitly refetches current metadata/bytes.
    async with Client(server, mode="2026-07-28", extensions=[SkillClient()], cache=None) as client:
        doctor = (await client.call_tool("rockwell_protocol_doctor", {})).structured_content
        assert doctor["skills"]["revision"] == catalog.revision
        assert (await listing(client)).skills
        assert (
            bytes_of(await client.read_resource("skill://rockwell-engineering/references/a.md"))
            == b"Changed\r\n"
        )


async def test_failed_and_concurrent_publications_preserve_the_last_coherent_snapshot():
    source = Source()
    catalog = AppGuidanceCatalog(source)
    old = catalog.revision
    raw = await catalog.read_bytes("skill://rockwell-engineering/references/a.md")
    source.files["rockwell-engineering"]["references/a.md"] = "Changed"
    source.corrupt = True
    with pytest.raises(ValueError, match="hash"):
        await catalog.refresh(old)
    assert catalog.revision == old
    assert await catalog.read_bytes("skill://rockwell-engineering/references/a.md") == raw
    source.corrupt = False
    outcomes = await asyncio.gather(
        catalog.refresh(old), catalog.refresh(old), return_exceptions=True
    )
    assert sum(isinstance(result, BrokerError) for result in outcomes) == 1
    assert catalog.revision != old


async def test_unchanged_refresh_uses_one_fresh_discovery_read_and_is_instance_scoped(capsys):
    source = Source()
    source.delay = 0.002
    before = time.perf_counter()
    catalog = AppGuidanceCatalog(source)
    initial_ms = (time.perf_counter() - before) * 1000
    full_reads = len(source.calls)
    source.calls.clear()
    before = time.perf_counter()
    unchanged = await catalog.refresh(catalog.revision)
    refresh_ms = (time.perf_counter() - before) * 1000
    assert not unchanged["changed"] and len(source.calls) == 1 and full_reads == 8
    assert refresh_ms < initial_ms
    other = Source()
    other.files["rockwell-engineering"]["references/a.md"] = "Other connection"
    independent = AppGuidanceCatalog(other)
    assert independent.revision != catalog.revision
    print(
        json.dumps(
            {
                "synthetic_full_reads": full_reads,
                "unchanged_reads": 1,
                "full_ms": round(initial_ms, 2),
                "unchanged_ms": round(refresh_ms, 2),
            }
        )
    )


async def test_cancelled_notification_does_not_undo_committed_metadata_or_lose_its_bindings():
    source = Source()
    catalog = AppGuidanceCatalog(source)
    revision = catalog.revision
    source.files["rockwell-engineering"]["references/a.md"] = "Committed replacement"
    notifying = asyncio.Event()

    async def slow_listener():
        assert catalog.doctor()["skills"]["source_workflows"] is None
        notifying.set()
        await asyncio.Event().wait()

    catalog.listeners.add(slow_listener)
    operation = asyncio.create_task(catalog.refresh(revision))
    async with asyncio.timeout(3):
        await notifying.wait()
    operation.cancel()
    with pytest.raises(asyncio.CancelledError):
        await operation
    assert catalog.revision != revision
    assert catalog.doctor()["skills"]["source_workflows"] == workflow_keys(
        source("/api/broker/capabilities")
    )
    assert (
        await catalog.read_bytes("skill://rockwell-engineering/references/a.md")
        == b"Committed replacement"
    )


def test_real_canonical_sources_have_three_distinct_names_and_no_duplicate_maintained_copy():
    root = Path(__file__).resolve().parents[2]
    source = Source()
    source.files = {}
    for name in sorted(GUIDES):
        folder = root / "workflows" / name
        source.files[name] = {
            path.relative_to(folder).as_posix(): path.read_bytes().decode("utf-8")
            for path in folder.rglob("*.md")
        }
        assert not (root / "skills" / name / "references").exists()
    catalog = AppGuidanceCatalog(source)
    assert len(catalog.resource_metadata()) == 10
    assert len({entry["frontmatter"]["description"] for entry in catalog._entries.values()}) == 3


def test_legacy_backend_bad_identity_path_and_empty_catalog_fail_closed():
    source = Source()
    source.legacy = True
    with pytest.raises(ValueError, match="canonical skill_source"):
        AppGuidanceCatalog(source)
    source = Source()
    source.files.clear()
    with pytest.raises(ValueError, match="nonempty"):
        AppGuidanceCatalog(source)
    source = Source()
    source.files["rockwell-engineering"]["references/../../bad.md"] = "bad"
    with pytest.raises(ValueError, match="reference ID"):
        AppGuidanceCatalog(source)


def test_source_change_while_fetching_cannot_publish_mixed_workflows():
    source = Source()
    count = [0]

    def moving(path, params=None):
        if path.endswith("capabilities"):
            count[0] += 1
            if count[0] == 2:
                source.files["rockwell-engineering"]["references/a.md"] = "Moved"
        return source(path, params)

    with pytest.raises(ValueError, match="changed during publication"):
        fetch_snapshot(moving)


def test_connection_is_local_authenticated_bounded_and_does_not_follow_redirects(
    tmp_path, monkeypatch
):
    local = tmp_path / ".local"
    local.mkdir()
    token = local / "service.key"
    token.write_text("synthetic-secret")
    connection = local / "connection.json"
    connection.write_text(json.dumps({"url": "http://127.0.0.1:1", "tokenFile": str(token)}))
    source = GuidanceSource(tmp_path)
    real_client = httpx.Client
    seen = []

    def handler(request):
        seen.append(request)
        assert request.headers["Authorization"] == "Bearer synthetic-secret"
        return httpx.Response(302, headers={"Location": "https://invalid.example/"})

    monkeypatch.setattr(
        httpx,
        "Client",
        lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs),
    )
    with pytest.raises(ValueError, match="app connection"):
        source("/api/broker/capabilities")
    assert len(seen) == 1
    connection.write_text(json.dumps({"url": "https://external.example", "tokenFile": str(token)}))
    with pytest.raises(ValueError, match="loopback"):
        GuidanceSource(tmp_path)


@pytest.mark.parametrize("command", ["serve", "frontend"])
@pytest.mark.parametrize("enabled", [False, True])
def test_cli_skills_profile_is_explicit_on_both_endpoints(command, enabled, tmp_path, monkeypatch):
    from rockwell_framework import __main__ as entry

    selected = []
    sentinel = object()
    monkeypatch.setattr(entry, "check_versions", lambda: None)
    monkeypatch.setattr(entry, "verify", lambda _: {"app_root": str(tmp_path)})
    monkeypatch.setattr(entry, "profile", lambda _: {"app_root": str(tmp_path)})
    monkeypatch.setattr(entry, "GuidanceSource", lambda _: "selected-app")
    monkeypatch.setattr(entry, "AppGuidanceCatalog", lambda _: sentinel)
    monkeypatch.setattr(entry, "load_settings", lambda _: None)
    monkeypatch.setattr(entry, "RemoteBackend", lambda _: None)
    monkeypatch.setattr(
        entry, "serve_application", lambda *args, **kw: selected.append(kw["skills"])
    )

    class Server:
        def run(self, **kw):
            assert kw == {"transport": "stdio"}

    def surface(*args, **kw):
        selected.append(kw["skills"])
        return Server()

    monkeypatch.setattr(entry, "create_surface", surface)
    monkeypatch.setattr(entry, "run_frontend", lambda server, root: server.run(transport="stdio"))
    monkeypatch.setattr(
        "sys.argv",
        ["rockwell-framework", "--state", str(tmp_path), command]
        + (["--skills"] if enabled else []),
    )
    entry.main()
    assert selected == [sentinel if enabled else None]
