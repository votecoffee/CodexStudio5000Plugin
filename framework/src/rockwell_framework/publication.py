"""Explicit local publication, never a model-facing executable installation tool."""

from __future__ import annotations

import hashlib
import importlib.metadata
import json
import os
import shutil
import uuid
from importlib.resources import files
from pathlib import Path

from universal_tools.cli import initialize
from universal_tools.daemon import InstanceLock
from universal_tools.framework import ApplicationDefinition
from universal_tools.models import atomic_json, load_settings

APP_ID = "rockwell-mcp-server"
INSTRUCTIONS = (
    "Discover Rockwell tools and workflow capabilities with search_tools/describe_tool. "
    "Read describe_service initialization guidance and the exact named workflow before work. "
    "Keep framework catalog revisions, extension generations and project/review hashes separate. "
    "App authorization and controller scope remain authoritative. Never replay an uncertain write."
)


def baseline():
    return json.loads(files("rockwell_framework").joinpath("baseline.json").read_text("utf-8"))


def check_versions():
    expected = baseline()
    actual = {"universal-codex-tools": expected["version"], "mcp": expected["mcp_python_sdk"]}
    for package, version in actual.items():
        if importlib.metadata.version(package) != version:
            raise ValueError(f"Use the locked app-owned environment: {package}=={version}")
    return expected


def ordinary(path):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        if item.is_symlink() or getattr(item, "is_junction", lambda: False)():
            raise ValueError("Framework state and deployments cannot traverse links/junctions")
    return path.resolve()


def digest(value):
    return hashlib.sha256(
        json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def inventory(directory):
    directory = ordinary(directory)
    result = []
    total = 0
    for path in sorted(directory.rglob("*")):
        ordinary(path)
        if path.is_file():
            size = path.stat().st_size
            total += size
            if len(result) >= 2048 or total > 256 * 1024 * 1024:
                raise ValueError("Use a deployment of at most 2048 files / 256 MiB")
            checksum = hashlib.sha256()
            with path.open("rb") as stream:
                for chunk in iter(lambda: stream.read(65536), b""):
                    checksum.update(chunk)
            result.append(
                {
                    "path": path.relative_to(directory).as_posix(),
                    "bytes": size,
                    "sha256": checksum.hexdigest(),
                }
            )
    if not result or len(result) > 2048 or sum(p["bytes"] for p in result) > 256 * 1024 * 1024:
        raise ValueError("Use a nonempty MCP deployment of at most 2048 files / 256 MiB")
    return result


def profile(state):
    state = ordinary(state)
    marker = json.loads((state / "rockwell-profile.json").read_text("utf-8"))
    if marker.get("schema_version") != 1 or marker.get("application_id") != APP_ID:
        raise ValueError("Not a dedicated Rockwell framework state")
    if not Path(marker["app_root"]).is_absolute() or not ordinary(marker["app_root"]).is_dir():
        raise ValueError("Invalid app root in framework profile")
    return marker


def verify(state):
    check_versions()
    state = ordinary(state)
    marker = profile(state)
    publication = ApplicationDefinition.model_validate_json(
        (state / "application.json").read_bytes()
    )
    if publication.id != APP_ID or len(publication.backends) != 1:
        raise ValueError("Unexpected app publication")
    backend = publication.backends[0]
    if backend.artifact is None or backend.service.transport.type != "stdio":
        raise ValueError("Use the pinned Rockwell stdio adapter")
    deployment = ordinary(Path(backend.artifact.path).parent)
    if deployment.parent != state / "deployments":
        raise ValueError("Backend must be an app-owned immutable deployment")
    expected = json.loads(deployment.with_suffix(".inventory.json").read_text("utf-8"))
    if inventory(deployment) != expected or deployment.name != digest(expected):
        raise ValueError("Backend deployment differs from its complete file inventory")
    transport = backend.service.transport
    if (
        transport.command != str(deployment / "Studio5000.Mcp.exe")
        or transport.args
        or transport.cwd != marker["app_root"]
        or transport.env_vars != ["STUDIO5000_PLUGIN_ROOT"]
        or backend.service.id != APP_ID
        or backend.service.session_mode != "transient"
    ):
        raise ValueError("Unexpected Rockwell backend routing")
    backend.verify()
    return marker


def publish(app_root, state, *, backend_root=None, port=None, allowed_write_tools=()):
    check_versions()
    state = ordinary(state)
    if (
        state.exists()
        and not (state / "rockwell-profile.json").exists()
        and any(p.name != "publication.lock" for p in state.iterdir())
    ):
        raise ValueError("Use a new dedicated state, never universal broker or workspace state")
    state.mkdir(parents=True, exist_ok=True)
    ordinary(state / "publication.lock")
    with InstanceLock(state / "publication.lock"):
        return _publish(
            app_root,
            state,
            backend_root=backend_root,
            port=port,
            allowed_write_tools=allowed_write_tools,
        )


def _publish(app_root, state, *, backend_root, port, allowed_write_tools):
    check_versions()
    app_root, state = ordinary(app_root), ordinary(state)
    if not app_root.is_dir():
        raise ValueError("Select an existing app root")
    marker_path = state / "rockwell-profile.json"
    if marker_path.exists():
        if profile(state)["app_root"] != str(app_root):
            raise ValueError("Framework state is already bound to another app root")
    elif any(p.name != "publication.lock" for p in state.iterdir()):
        raise ValueError("Use a new dedicated state, never universal broker or workspace state")
    source = ordinary(backend_root or app_root / "artifacts/app/Mcp")
    if state.is_relative_to(source):
        raise ValueError("Framework state must be outside the MCP adapter directory")
    members = inventory(source)
    required = {
        "Studio5000.Mcp.exe",
        "Studio5000.Mcp.dll",
        "Studio5000.Mcp.deps.json",
        "Studio5000.Mcp.runtimeconfig.json",
    }
    if not required.issubset({p["path"] for p in members}):
        raise ValueError("Select a complete published .NET MCP adapter")
    destination = state / "deployments" / digest(members)
    if os.name == "nt" and max(len(str(destination / p["path"])) for p in members) > 240:
        raise ValueError(
            "Choose a shorter framework state path; this native adapter deployment would exceed 240 characters"
        )
    # Validate before publishing configuration or initializing private runtime settings.
    service = {
        "schema_version": 1,
        "id": APP_ID,
        "display_name": "Rockwell engineering backend",
        "description": "Explicit project/review operations against the shared Rockwell service.",
        "transport": {
            "type": "stdio",
            "command": str(destination / "Studio5000.Mcp.exe"),
            "args": [],
            "cwd": str(app_root),
            "env_vars": ["STUDIO5000_PLUGIN_ROOT"],
        },
        "enabled": True,
        "session_mode": "transient",
        "resource_id": "rockwell-" + digest(os.path.normcase(str(app_root)))[:24],
        "allow_writes": bool(allowed_write_tools),
        "allowed_write_tools": list(allowed_write_tools),
        "max_argument_bytes": 8388608,
        "timeout_seconds": 60,
        "capability_catalog": {
            "schema_version": 1,
            "tool": "rockwell_broker_capabilities",
            "arguments": {},
        },
    }
    entry = next(p for p in members if p["path"] == "Studio5000.Mcp.exe")
    document = {
        "schema_version": 1,
        "id": APP_ID,
        "display_name": "RockwellMCPServer",
        "instructions": INSTRUCTIONS,
        "backends": [
            {
                "revision": digest(service),
                "service": service,
                "artifact": {"path": service["transport"]["command"], "sha256": entry["sha256"]},
            }
        ],
    }
    ApplicationDefinition.model_validate(document)
    state.mkdir(parents=True, exist_ok=True)
    if not marker_path.exists():
        atomic_json(
            marker_path, {"schema_version": 1, "application_id": APP_ID, "app_root": str(app_root)}
        )
    destination.parent.mkdir(exist_ok=True)
    if destination.exists():
        if inventory(destination) != members:
            raise ValueError("Existing immutable backend deployment changed")
    else:
        temporary = destination.parent / (".staging-" + uuid.uuid4().hex)
        try:
            shutil.copytree(source, temporary)
            if inventory(temporary) != members:
                raise ValueError(
                    "Backend changed while copying; publish again from a stopped build"
                )
            temporary.rename(destination)
        finally:
            if temporary.exists():
                if ordinary(
                    temporary
                ).parent != destination.parent or not temporary.name.startswith(".staging-"):
                    raise ValueError("Refusing cleanup outside the owned staging directory")
                shutil.rmtree(temporary)
    atomic_json(destination.with_suffix(".inventory.json"), members)
    # Framework initialize preserves an existing endpoint/credential rather than replacing it.
    selected_port = (
        port
        if port is not None
        else load_settings(state).port
        if (state / "settings.json").exists()
        else 8891
    )
    initialize(state, selected_port)
    atomic_json(state / "application.json", document)
    verify(state)
    return document
