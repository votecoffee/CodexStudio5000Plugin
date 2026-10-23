"""Disposable immutable-backend tests; no Rockwell engineering process is run."""
import hashlib
import json
import os
import shutil
import subprocess
import time
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
SHELLS = [p for name in ("powershell.exe", "pwsh.exe") if (p := shutil.which(name))]
pytestmark = pytest.mark.skipif(os.name != "nt" or not SHELLS, reason="Windows PowerShell required")


@pytest.fixture(scope="session")
def harmless_backend(tmp_path_factory):
    output = tmp_path_factory.mktemp("rockwell-launcher-binary") / "published"
    result = subprocess.run(
        ["dotnet", "publish", str(ROOT / "tests/Studio5000.LauncherFixture/Studio5000.LauncherFixture.csproj"),
         "-c", "Release", "-o", str(output), "--nologo", "--verbosity", "quiet"],
        capture_output=True, text=True, encoding="utf-8", timeout=90,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    return output


def prepare(tmp_path, harmless_backend):
    cache = tmp_path / "disposable-codex-cache"
    source = cache / "version/Mcp"
    source.mkdir(parents=True)
    for p in harmless_backend.iterdir():
        if p.is_file():
            shutil.copyfile(p, source / p.name)
    shutil.copyfile(source / "Rockwell.LauncherFixture.exe", source / "Studio5000.StdioHost.exe")
    for name in ("Studio5000.StdioHost.dll", "Studio5000.StdioHost.deps.json",
                 "Studio5000.StdioHost.runtimeconfig.json", "Studio5000.Mcp.exe", "Studio5000.Mcp.dll",
                 "Studio5000.Mcp.deps.json", "Studio5000.Mcp.runtimeconfig.json", "Studio5000.Core.dll"):
        (source / name).write_bytes(b"Harmless inventory placeholder; never executed.")
    files = [{"path": "Mcp/" + p.name, "sha256": hashlib.sha256(p.read_bytes()).hexdigest(), "size": p.stat().st_size}
             for p in sorted(source.iterdir(), key=lambda p: p.name)]
    candidate = {"version": "26.1023.135453", "kind": "MCP frontend closure only", "state": "staged-not-selected",
                 "sourceTreeClean": True, "sourceCommit": "a" * 40, "payloads": files,
                 "coreSha256": next(p["sha256"] for p in files if p["path"] == "Mcp/Studio5000.Core.dll")}
    manifest = source.parent / "candidate.json"
    manifest.write_text(json.dumps(candidate), encoding="utf-8")
    app = tmp_path / "selected app caf\u00e9 \u6e2c\u8a66"
    app.mkdir()
    (app / "service.local.json").write_text('{"Studio5000":{"EnableControllerOperations":false}}', encoding="utf-8")
    state = tmp_path / "app owned caf\u00e9 \u6e2c\u8a66"
    fixture = tmp_path / "fixture"
    fixture.mkdir()
    (fixture / ".launcher-fixture").write_text("Only harmless stdio stand-in", encoding="utf-8")
    return {"cache": cache, "source": source, "manifest": manifest, "app": app, "state": state,
            "fixture": fixture, "manifest_sha": hashlib.sha256(manifest.read_bytes()).hexdigest()}


def publish(shell, case, *, expected="absent", plan=False):
    args = [shell, "-NoLogo", "-NoProfile", "-File", str(ROOT / "scripts/Publish-StableMcp.ps1"),
            "-SourceDirectory", str(case["source"]), "-CandidateManifest", str(case["manifest"]),
            "-ExpectedManifestSha256", case["manifest_sha"], "-AppRoot", str(case["app"]),
            "-StateRoot", str(case["state"]), "-ExpectedPointerSha256", expected]
    if plan:
        args.append("-PlanOnly")
    return subprocess.run(args, capture_output=True, text=True, encoding="utf-8", timeout=30)


def launch(shell, case, payload=""):
    return subprocess.run(
        [shell, "-NoLogo", "-NoProfile", "-File", str(case["state"] / "Start-Mcp.ps1")],
        input=payload, capture_output=True, text=True, encoding="utf-8", timeout=20,
        cwd=case["fixture"], env={**os.environ, "ROCKWELL_LAUNCHER_FIXTURE_ROOT": str(case["fixture"])},
    )


@pytest.mark.parametrize("shell", SHELLS)
def test_plan_writes_nothing_and_uses_app_owned_command_and_cwd(tmp_path, harmless_backend, shell):
    case = prepare(tmp_path, harmless_backend)
    result = publish(shell, case, plan=True)
    assert result.returncode == 0, result.stderr
    plan = json.loads(result.stdout)
    assert not case["state"].exists()
    assert plan["proposedBinding"]["cwd"] == str(case["state"])
    assert plan["proposedBinding"]["args"][-1] == str(case["state"] / "Start-Mcp.ps1")
    assert not plan["codexConfigChanged"] and not plan["backendExecuted"] and not plan["selected"]


@pytest.mark.parametrize("shell", SHELLS)
def test_cache_removal_unicode_and_eof_keep_the_exact_selected_app_profile(tmp_path, harmless_backend, shell):
    case = prepare(tmp_path, harmless_backend)
    settings = (case["app"] / "service.local.json").read_bytes()
    result = publish(shell, case)
    assert result.returncode == 0, result.stderr
    plan = json.loads(result.stdout)
    assert plan["selected"] and not (case["fixture"] / "started.json").exists()
    cache = case["cache"].resolve()
    assert cache.is_relative_to(tmp_path.resolve()) and cache != tmp_path.resolve()
    shutil.rmtree(cache)  # Only this test's verified disposable cache.
    payload = json.dumps({"jsonrpc": "2.0", "fixture": "caf\u00e9 \u6e2c\u8a66", "text": "[,], quote \\\""}, ensure_ascii=False) + "\n"
    output = launch(shell, case, payload)
    assert output.returncode == 0, output.stderr
    assert json.loads(output.stdout) == json.loads(payload)
    record = json.loads((case["fixture"] / "started.json").read_text(encoding="utf-8"))
    assert record["cwd"] == str(case["state"]) and record["appRoot"] == str(case["app"])
    assert record["arguments"] == [] and not record["nativeCodeExecuted"]
    assert (case["app"] / "service.local.json").read_bytes() == settings


@pytest.mark.parametrize("shell", SHELLS)
@pytest.mark.parametrize("tamper", ["backend", "extra", "module", "manifest", "pointer", "root"])
def test_drift_is_rejected_before_the_backend_runs(tmp_path, harmless_backend, shell, tamper):
    case = prepare(tmp_path, harmless_backend)
    result = publish(shell, case)
    assert result.returncode == 0, result.stderr
    plan = json.loads(result.stdout)
    snapshot = Path(plan["snapshot"])
    pointer = case["state"] / "launcher/current.json"
    if tamper == "backend":
        (snapshot / "Studio5000.Core.dll").write_bytes(b"tampered")
    elif tamper == "extra":
        (snapshot / "extra.dll").write_bytes(b"unreviewed")
    elif tamper == "module":
        (case["state"] / "StableMcp.psm1").write_bytes(b"unreviewed")
    elif tamper == "manifest":
        (snapshot / "backend.json").write_bytes(b"{}")
    else:
        selection = json.loads(pointer.read_text(encoding="utf-8"))
        selection["backend_id" if tamper == "pointer" else "app_root"] = "f" * 64 if tamper == "pointer" else str(tmp_path)
        pointer.write_text(json.dumps(selection), encoding="utf-8")
    output = launch(shell, case)
    assert output.returncode != 0 and not output.stdout
    assert not (case["fixture"] / "started.json").exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_revision_guard_and_idempotent_publication_preserve_prior_bytes(tmp_path, harmless_backend, shell):
    case = prepare(tmp_path, harmless_backend)
    first = publish(shell, case)
    assert first.returncode == 0, first.stderr
    plan = json.loads(first.stdout)
    pointer = case["state"] / "launcher/current.json"
    before = pointer.read_bytes()
    refused = publish(shell, case, expected="0" * 64)
    assert refused.returncode != 0 and pointer.read_bytes() == before
    repeated = publish(shell, case, expected=plan["pointerSha256"])
    assert repeated.returncode == 0, repeated.stderr
    assert pointer.read_bytes() == before
    assert len(list((case["state"] / "launcher/backends").iterdir())) == 1


@pytest.mark.parametrize("shell", SHELLS)
def test_stable_state_cannot_be_published_inside_a_codex_cache(tmp_path, harmless_backend, shell):
    case = prepare(tmp_path, harmless_backend)
    case["state"] = tmp_path / ".codex/plugins/cache/rockwell/version"
    refused = publish(shell, case)
    assert refused.returncode != 0 and not case["state"].exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_reviewed_manifest_and_member_paths_are_required_before_publication(tmp_path, harmless_backend, shell):
    case = prepare(tmp_path, harmless_backend)
    case["manifest"].write_text("{}", encoding="utf-8")
    refused = publish(shell, case)
    assert refused.returncode != 0 and not case["state"].exists()
    candidate = {"version": "26.101.10101", "kind": "MCP frontend closure only", "state": "staged-not-selected",
                 "sourceTreeClean": True, "sourceCommit": "a" * 40, "coreSha256": "a" * 64,
                 "payloads": [{"path": "Mcp/../outside.exe", "sha256": "a" * 64, "size": 1}]}
    case["manifest"].write_text(json.dumps(candidate), encoding="utf-8")
    case["manifest_sha"] = hashlib.sha256(case["manifest"].read_bytes()).hexdigest()
    refused = publish(shell, case)
    assert refused.returncode != 0 and not case["state"].exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_maintenance_hold_is_preserved_and_refuses_before_execution(tmp_path, harmless_backend, shell):
    case = prepare(tmp_path, harmless_backend)
    result = publish(shell, case)
    assert result.returncode == 0, result.stderr
    local = case["app"] / ".local"
    local.mkdir()
    hold = local / "maintenance-hold.json"
    hold.write_bytes(b"Preserved explicit fixture hold")
    output = launch(shell, case)
    assert output.returncode != 0 and hold.read_bytes() == b"Preserved explicit fixture hold"
    assert not (case["fixture"] / "started.json").exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_inspection_checks_the_staged_contract_without_starting_it(tmp_path, harmless_backend, shell):
    case = prepare(tmp_path, harmless_backend)
    result = publish(shell, case)
    assert result.returncode == 0, result.stderr
    inspected = subprocess.run(
        [shell, "-NoLogo", "-NoProfile", "-File", str(ROOT / "scripts/Inspect-StableMcp.ps1"),
         "-StateRoot", str(case["state"])], capture_output=True, text=True, encoding="utf-8", timeout=20,
    )
    assert inspected.returncode == 0, inspected.stderr
    report = json.loads(inspected.stdout)
    assert report["appRoot"] == str(case["app"]) and not report["backendExecuted"]
    assert not report["nativeAcceptanceVerified"] and not report["codexConfigurationChanged"]
    assert not (case["fixture"] / "started.json").exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_pointer_rotation_preserves_the_running_backend_and_retains_prior_selection(tmp_path, harmless_backend, shell):
    case = prepare(tmp_path, harmless_backend)
    first = publish(shell, case)
    assert first.returncode == 0, first.stderr
    old = json.loads(first.stdout)
    pointer = case["state"] / "launcher/current.json"
    previous = pointer.read_bytes()
    old_files = {p.relative_to(old["snapshot"]): p.read_bytes() for p in Path(old["snapshot"]).rglob("*") if p.is_file()}
    process = subprocess.Popen(
        [shell, "-NoLogo", "-NoProfile", "-File", str(case["state"] / "Start-Mcp.ps1")],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
        env={**os.environ, "ROCKWELL_LAUNCHER_FIXTURE_ROOT": str(case["fixture"])},
    )
    try:
        deadline = time.monotonic() + 10
        while not (case["fixture"] / "started.json").exists():
            assert process.poll() is None
            assert time.monotonic() < deadline
            time.sleep(0.02)
        changed = case["source"] / "Studio5000.Mcp.dll"
        changed.write_bytes(b"Second harmless immutable inventory")
        candidate = json.loads(case["manifest"].read_text(encoding="utf-8"))
        entry = next(f for f in candidate["payloads"] if f["path"] == "Mcp/Studio5000.Mcp.dll")
        entry.update(sha256=hashlib.sha256(changed.read_bytes()).hexdigest(), size=changed.stat().st_size)
        case["manifest"].write_text(json.dumps(candidate), encoding="utf-8")
        case["manifest_sha"] = hashlib.sha256(case["manifest"].read_bytes()).hexdigest()
        second = publish(shell, case, expected=old["pointerSha256"])
        assert second.returncode == 0, second.stderr
        assert json.loads(second.stdout)["backendId"] != old["backendId"]
        assert process.poll() is None
        assert {p.relative_to(old["snapshot"]): p.read_bytes() for p in Path(old["snapshot"]).rglob("*") if p.is_file()} == old_files
        history = case["state"] / "launcher/history" / ("selection-" + old["pointerSha256"] + ".json")
        assert history.read_bytes() == previous
        stdout, stderr = process.communicate('{"fixture":"EOF after pointer rotation"}\n', timeout=10)
        assert process.returncode == 0 and json.loads(stdout)["fixture"] == "EOF after pointer rotation", stderr
    finally:
        if process.poll() is None:
            process.stdin.close()
            process.wait(timeout=10)
