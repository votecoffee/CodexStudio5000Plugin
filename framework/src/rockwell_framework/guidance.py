"""Explicit publication of this app's hash-bound canonical workflow metadata only."""

from __future__ import annotations

import asyncio
import hashlib
import ipaddress
import json
import re
import tempfile
from contextlib import contextmanager
from pathlib import Path
from urllib.parse import urlsplit

import httpx
from mcp.types import CallToolResult, ToolAnnotations
from universal_tools.models import BrokerError
from universal_tools.skill_catalog import SkillCatalog
from universal_tools.skill_manifest import OPENAI_IMPORT_PROFILE, frontmatter
from universal_tools.surface import json_result

from .management import read_metadata
from .publication import ordinary

GUIDES = frozenset({"rockwell-engineering", "logix-online-edits", "factorytalk-studio"})
MAX_BYTES = 512 * 1024


def checksum(raw):
    return hashlib.sha256(raw).hexdigest()


class GuidanceSource:
    """No caller URLs, paths, targets or credentials; reuse the selected app connection."""

    def __init__(self, root):
        root = ordinary(root)
        connection = read_metadata(root / ".local/connection.json")
        parsed = urlsplit(connection["url"])
        if (
            parsed.scheme != "http"
            or not ipaddress.ip_address(parsed.hostname).is_loopback
            or parsed.path not in ("", "/")
            or parsed.query
            or parsed.fragment
            or parsed.username
        ):
            raise ValueError("Use the existing loopback Rockwell connection")
        token_file = ordinary(connection["tokenFile"])
        if not token_file.is_relative_to(root / ".local"):
            raise ValueError("Use the app-owned connection credential")
        token = token_file.read_text("utf-8-sig").strip()
        self.url = connection["url"].rstrip("/")
        self.headers = {"Authorization": "Bearer " + token}

    def __call__(self, path, params=None):
        # Metadata GET only. Redirects, environment proxies and operation retries are disabled.
        try:
            with (
                httpx.Client(timeout=5, trust_env=False, follow_redirects=False) as client,
                client.stream(
                    "GET", self.url + path, params=params, headers=self.headers
                ) as response,
            ):
                response.raise_for_status()
                raw = bytearray()
                for chunk in response.iter_bytes():
                    raw.extend(chunk)
                    if len(raw) > MAX_BYTES:
                        raise ValueError("Guidance metadata exceeds its byte budget")
            return json.loads(raw)
        except (httpx.HTTPError, json.JSONDecodeError):
            raise ValueError(
                "Read current Rockwell guidance failed; verify the app connection"
            ) from None


def workflow_keys(document):
    if document.get("schema_version") != 1:
        raise ValueError("Unsupported app guidance catalog")
    entries = document.get("capabilities", [])
    if not isinstance(entries, list) or len(entries) > 1000:
        raise ValueError("Invalid app guidance catalog")
    result = {}
    for entry in entries:
        key = entry.get("id", "")
        if not key.startswith("workflow."):
            continue
        name = key.removeprefix("workflow.")
        discovery = entry.get("discovery", {})
        args = discovery.get("arguments", {})
        revision = args.get("expectedRevision", "")
        if (
            name not in GUIDES
            or name in result
            or discovery.get("tool") != "rockwell_read_workflow"
            or args.get("workflowId") != name
            or not re.fullmatch(r"[a-f0-9]{64}", revision)
        ):
            raise ValueError("Invalid or unsupported app workflow binding")
        result[name] = revision
    if not result:
        raise ValueError("Rockwell guidance requires a nonempty real workflow catalog")
    return result


def verified_text(document, text_key, expected_hash=None):
    text = document.get(text_key)
    if not isinstance(text, str):
        raise ValueError("New Rockwell backend with canonical skill_source is required")  # noqa: TRY004
    raw = text.encode("utf-8")
    if len(raw) > 256 * 1024 or checksum(raw) != document.get("sha256"):
        raise ValueError("App guidance content/hash mismatch")
    if expected_hash is not None and checksum(raw) != expected_hash:
        raise ValueError("App guidance reference/hash mismatch")
    return raw


def fetch_snapshot(fetch, previous_keys=None):
    """One discovery read for unchanged guides; revisions always come from the app."""
    keys = workflow_keys(fetch("/api/broker/capabilities"))
    if keys == previous_keys:
        return keys, None
    snapshot = {}
    for name, revision in sorted(keys.items()):
        args = {"workflowId": name, "expectedRevision": revision}
        guide = fetch("/api/broker/workflow", args)
        if guide.get("id") != name or guide.get("revision") != revision:
            raise ValueError("App workflow identity changed")
        raw = verified_text(guide.get("skill_source", {}), "text")
        if frontmatter(raw).get("name") != name:
            raise ValueError("App skill name does not match its workflow")
        files = {"SKILL.md": raw}
        references = guide.get("references", [])
        if not isinstance(references, list) or len(references) > 16:
            raise ValueError("App guidance reference budget exceeded")
        for reference in references:
            name_ref = reference.get("id", "")
            path = "references/" + name_ref + ".md"
            if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", name_ref) or path in files:
                raise ValueError("Invalid or duplicate app reference ID")
            sha = reference.get("sha256", "")
            result = fetch(
                "/api/broker/reference", {**args, "referenceId": name_ref, "expectedSha256": sha}
            )
            if result.get("id") != name_ref or result.get("revision") != revision:
                raise ValueError("App reference identity changed")
            files[path] = verified_text(result, "content", sha)
        snapshot[name] = files
    if sum(len(raw) for files in snapshot.values() for raw in files.values()) > MAX_BYTES:
        raise ValueError("Combined guidance exceeds its byte budget")
    # Never publish a mixture if the backend was replaced during the metadata reads.
    if workflow_keys(fetch("/api/broker/capabilities")) != keys:
        raise ValueError("App workflows changed during publication; rediscover explicitly")
    return keys, snapshot


@contextmanager
def materialize(snapshot):
    # SkillCatalog snapshots the bytes. Nothing is installed into native skill discovery.
    with tempfile.TemporaryDirectory(prefix="rockwell-guidance-") as directory:
        roots = []
        for name, files in snapshot.items():
            root = Path(directory) / name
            for relative, raw in files.items():
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(raw)
            roots.append(root)
        yield roots


class AppGuidanceCatalog(SkillCatalog):
    def __init__(self, fetch, *, page_size=16):
        self.fetch = fetch
        self.source_keys, snapshot = fetch_snapshot(fetch)
        if set(self.source_keys) != GUIDES:
            raise ValueError("Initial Rockwell profile requires all three distinct workflows")
        with materialize(snapshot) as roots:
            super().__init__(roots, profile=OPENAI_IMPORT_PROFILE, page_size=page_size)
        self.publication_lock = asyncio.Lock()

    async def refresh(self, expected_revision):
        async with self.publication_lock:
            if expected_revision != self.revision:
                raise BrokerError("skill publication revision changed")
            keys, snapshot = await asyncio.to_thread(fetch_snapshot, self.fetch, self.source_keys)
            if snapshot is None:
                return {"changed": False, "revision": self.revision, "generation": self.generation}
            with materialize(snapshot) as roots:
                generation = self.generation
                try:
                    await self.publish(roots, expected_revision=expected_revision)
                finally:
                    # Cancellation during best-effort notification delivery can follow a
                    # committed publication. Keep its source bindings even without a reply.
                    if self.generation != generation:
                        self.source_keys = keys
            return {"changed": True, "revision": self.revision, "generation": self.generation}

    def doctor(self):
        return {
            "schema_version": 1,
            "profile": "app-owned-framework-skills",
            "selected_standalone_route_changed": False,
            "skills": {
                "state": "enabled-and-tested",
                "revision": self.revision,
                "generation": self.generation,
                "source_workflows": None
                if self.publication_lock.locked()
                else dict(self.source_keys),
                "publication_in_progress": self.publication_lock.locked(),
                "methods": ["skills/list", "skills/get", "resources/read"],
                "directory_read": False,
                "publication": "Explicit expected-revision refresh; immutable bytes until publication",
            },
            "notifications": "Resource list/generation hints; reconnect and refetch after loss",
            "discovery_cache": "Unchanged workflow revisions reuse local bytes after one fresh app discovery read",
            "tasks": "blocked: no app-owned trusted owner and durable task-to-job start-intent binding",
            "progress": "blocked: no native job-to-request progress relay on this selected adapter",
            "cancellation": "Use ordinary SDK/extension job cancellation and inspect the real eventual outcome",
            "approval_continuations": "blocked: engineering review tokens have no app-owned elicitation resolver",
            "engineering_artifact_resources": "blocked: no owner-bound resource adapter; ordinary artifact readers remain",
            "native_codex_activation": "unassessed: MCP publication does not activate native skills",
        }

    def install(self, server):
        @server.tool(annotations=ToolAnnotations(read_only_hint=True, open_world_hint=False))
        async def rockwell_protocol_doctor() -> CallToolResult:
            """Read profile, guidance revision and concrete protocol acceptance gaps; no engineering I/O."""
            return json_result(self.doctor())

        @server.tool(annotations=ToolAnnotations(read_only_hint=False, open_world_hint=False))
        async def rockwell_refresh_guidance(expectedRevision: str) -> CallToolResult:
            """Publish current app-owned guidance atomically at this exact catalog revision. Metadata only; no approval, engineering operation, native skill activation or route switch. Fetch the revision from rockwell_protocol_doctor first."""
            return json_result(await self.refresh(expectedRevision))
