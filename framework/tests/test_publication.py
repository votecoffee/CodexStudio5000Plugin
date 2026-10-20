import json
import uuid
from pathlib import Path

import pytest
from universal_tools.daemon import InstanceLock
from universal_tools.models import BrokerError

from rockwell_framework.publication import inventory, publish, verify


@pytest.fixture
def app(tmp_path):
    root = tmp_path / "generated app"
    backend = root / "artifacts/app/Mcp"
    backend.mkdir(parents=True)
    for name in (
        "Studio5000.Mcp.exe",
        "Studio5000.Mcp.dll",
        "Studio5000.Mcp.deps.json",
        "Studio5000.Mcp.runtimeconfig.json",
        "dependency.dll",
    ):
        (backend / name).write_bytes(b"generated publication fixture; never executed")
    return root, tmp_path.parent / ("fwk-" + uuid.uuid4().hex[:12]), backend


def test_read_only_publication_preserves_source_and_pins_complete_deployment(app):
    root, state, source = app
    before = inventory(source)
    document = publish(root, state)
    service = document["backends"][0]["service"]
    assert service["session_mode"] == "transient"
    assert service["protocol_mode"] == "legacy"
    assert service["discovery_cache_ttl_seconds"] == 0
    assert service["watch_catalog"] is False
    assert service["max_argument_depth"] == 64
    assert service["max_argument_nodes"] == 100000
    assert not service["allow_writes"] and service["allowed_write_tools"] == []
    assert service["transport"]["env_vars"] == ["STUDIO5000_PLUGIN_ROOT"]
    assert document["backends"][0]["artifact"]["path"] != str(source / "Studio5000.Mcp.exe")
    assert verify(state)["app_root"] == str(root)
    assert inventory(source) == before
    assert not (root / ".local").exists()
    assert not (state / "framework/active.json").exists()


def test_full_closure_tampering_fails_even_with_unchanged_executable(app):
    root, state, _ = app
    document = publish(root, state)
    deployed = Path(document["backends"][0]["artifact"]["path"]).parent
    (deployed / "dependency.dll").write_bytes(b"changed dependency")
    with pytest.raises(ValueError, match="inventory"):
        verify(state)


def test_republication_is_idempotent_and_policy_changes_have_distinct_revisions(app):
    root, state, _ = app
    first = publish(root, state, port=8892)
    credentials = (state / "settings.json").read_bytes()
    assert publish(root, state) == first
    changed = publish(root, state, allowed_write_tools=["studio_register_project"])
    assert changed["backends"][0]["revision"] != first["backends"][0]["revision"]
    assert changed["backends"][0]["service"]["allowed_write_tools"] == ["studio_register_project"]
    assert (state / "settings.json").read_bytes() == credentials


def test_state_cannot_adopt_another_workspace_or_universal_registrations(app, tmp_path):
    root, state, source = app
    publish(root, state)
    other = tmp_path / "another app"
    other.mkdir()
    with pytest.raises(ValueError, match="another app"):
        publish(other, state, backend_root=source)
    broker = tmp_path / "existing broker"
    broker.mkdir()
    (broker / "settings.json").write_text("{}")
    with pytest.raises(ValueError, match="dedicated state"):
        publish(root, broker)
    assert sorted(p.name for p in broker.iterdir()) == ["settings.json"]


def test_incomplete_or_invalid_policy_preserves_existing_publication(app):
    root, state, source = app
    publish(root, state)
    before = (state / "application.json").read_bytes()
    with pytest.raises(ValueError):
        publish(root, state, allowed_write_tools=["*"])
    (source / "Studio5000.Mcp.dll").unlink()
    with pytest.raises(ValueError, match="complete published"):
        publish(root, state)
    assert (state / "application.json").read_bytes() == before


def test_concurrent_publishers_take_an_os_lock(app):
    root, state, _ = app
    publish(root, state)
    with InstanceLock(state / "publication.lock"), pytest.raises(BrokerError):
        publish(root, state)


def test_junction_or_link_is_rejected_before_copying(app, tmp_path):
    root, state, source = app
    link = tmp_path / "linked backend"
    import os
    import subprocess

    if os.name == "nt":
        subprocess.run(
            ["cmd", "/c", "mklink", "/J", str(link), str(source)],
            check=True,
            capture_output=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
    else:
        link.symlink_to(source, target_is_directory=True)
    with pytest.raises(ValueError, match="links/junctions"):
        publish(root, state, backend_root=link)


def test_route_tampering_is_rejected(app):
    root, state, _ = app
    document = publish(root, state)
    document["backends"][0]["service"]["transport"]["args"] = ["unexpected"]
    (state / "application.json").write_text(json.dumps(document))
    with pytest.raises(ValueError, match="routing"):
        verify(state)


def test_dependency_versions_are_checked_without_downgrading(app, monkeypatch):
    import rockwell_framework.publication as module

    root, state, _ = app
    original = module.importlib.metadata.version
    monkeypatch.setattr(
        module.importlib.metadata,
        "version",
        lambda name: "2.2.0" if name == "mcp" else original(name),
    )
    with pytest.raises(ValueError, match="mcp==2.3.0"):
        publish(root, state)
    assert not (state / "application.json").exists()


@pytest.mark.parametrize(
    "field,value",
    [
        ("protocol_mode", "auto"),
        ("discovery_cache_ttl_seconds", 10),
        ("max_argument_depth", 65),
        ("max_argument_nodes", 100001),
    ],
)
def test_unassessed_protocol_or_budget_changes_are_rejected(app, field, value):
    root, state, _ = app
    document = publish(root, state)
    document["backends"][0]["service"][field] = value
    (state / "application.json").write_text(json.dumps(document))
    with pytest.raises(ValueError, match="routing"):
        verify(state)


@pytest.mark.skipif(__import__("os").name != "nt", reason="Native Windows launch boundary")
def test_deployment_path_limit_fails_before_copying(app, tmp_path):
    root, _, _ = app
    state = tmp_path / ("generated-long-state-" + "x" * 60)
    with pytest.raises(ValueError, match="shorter framework state"):
        publish(root, state)
    assert not (state / "application.json").exists()
    assert not (state / "deployments").exists()
