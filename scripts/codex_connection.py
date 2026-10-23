"""Read-only readiness and explicit cached-launcher repair; Python 3.11+, stdlib only.

Never launches MCP, installs packages, changes engineering policy or probes a PLC.
Only `repair --apply` writes, after an exact configuration hash review.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import ipaddress
import itertools
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import tomllib
import urllib.request
from urllib.parse import urlsplit

PLUGIN = "rockwell-mcp-server@rockwell-mcp-server-local"
SERVER = "rockwell-mcp-server"
MAXIMUM_BYTES = 2 * 1024 * 1024
POLICY_KEYS = ("EnableControllerOperations", "EnableSdkServiceRecovery", "AutoStartSdkService")


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read(path: Path, maximum: int = MAXIMUM_BYTES) -> bytes:
    for parent in (path, *path.parents):
        if parent.exists() and getattr(parent.stat(follow_symlinks=False), "st_file_attributes", 0) & 0x400:
            raise ValueError("Reparse points are not supported for connection evidence.")
        if parent.is_symlink():
            raise ValueError("Symbolic links are not supported for connection evidence.")
    with path.open("rb") as stream:
        value = stream.read(maximum + 1)
    if len(value) > maximum:
        raise ValueError("Connection evidence exceeds its size limit.")
    return value


def load(path: Path) -> dict:
    return json.loads(read(path))


def inventory(home: Path) -> dict:
    executable = shutil.which("codex")
    if not executable:
        raise ValueError("Codex CLI is unavailable; select its installed profile first.")
    environment = dict(os.environ)
    environment["CODEX_HOME"] = str(home)
    result = subprocess.run([executable, "plugin", "list", "--json"], capture_output=True, env=environment,
                            timeout=20, check=True, creationflags=0x08000000 if os.name == "nt" else 0)
    if len(result.stdout) > MAXIMUM_BYTES:
        raise ValueError("Codex inventory exceeds its size limit.")
    matches = [p for p in json.loads(result.stdout)["installed"] if p.get("pluginId") == PLUGIN and p.get("installed")]
    if len(matches) != 1:
        raise ValueError("Exactly one installed app-owned Rockwell plugin is required.")
    return matches[0]


def cached_package(root: Path, home: Path, plugin: dict) -> tuple[Path, dict]:
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", plugin["version"]):
        raise ValueError("Unexpected installed package version syntax.")
    cache = home / "plugins/cache/rockwell-mcp-server-local/rockwell-mcp-server" / plugin["version"]
    source = Path(plugin["source"]["path"])
    expected_parent = root / ".local/codex-marketplace/packages"
    if source.parent.resolve() != expected_parent.resolve() or plugin["source"].get("source") != "local":
        raise ValueError("The installed plugin belongs to another installation.")
    names = {str(p.relative_to(cache)).replace("\\", "/") for p in cache.rglob("*") if p.is_file()}
    source_names = {str(p.relative_to(source)).replace("\\", "/") for p in source.rglob("*") if p.is_file()}
    allowed = {"plugin.json", ".codex-plugin/plugin.json", "mcp.json", ".mcp.json", "LICENSE", "release-version.json",
               "runtime.json", "scripts/Start-Mcp.ps1", *(f"skills/{n}/SKILL.md" for n in
               ("rockwell-engineering", "logix-online-edits", "factorytalk-studio"))}
    if names != allowed or source_names != allowed:
        raise ValueError("Installed/cache package content differs from the eleven-file public allowlist.")
    hashes = {name: sha(read(cache / name)) for name in sorted(names)}
    if any(hashes[name] != sha(read(source / name)) for name in names):
        raise ValueError("Immutable package and installed cache bytes differ.")
    pointer = load(cache / "runtime.json")
    if pointer.get("schemaVersion") != 1 or Path(pointer["root"]).resolve() != root.resolve():
        raise ValueError("Cached runtime pointer does not select this installation.")
    release = load(cache / "release-version.json")
    if release["packageVersion"] != plugin["version"]:
        raise ValueError("Cached release and installed package identities differ.")
    return cache, {"version": plugin["version"], "displayVersion": release["displayVersion"],
                   "enabledInInventory": plugin.get("enabled"), "payloadSha256": hashes,
                   "bootstrap": "cached launcher delegates to the installed checkout scripts; not a self-contained binary package"}


def header_path(line: str) -> tuple[str, ...] | None:
    if not line.lstrip().startswith("[") or line.lstrip().startswith("[["):
        return None
    try:
        node = tomllib.loads(line + "\n__rockwell_header_probe__ = true\n")
        result = []
        while "__rockwell_header_probe__" not in node:
            if len(node) != 1:
                return None
            key, node = next(iter(node.items()))
            result.append(key)
        return tuple(result)
    except (tomllib.TOMLDecodeError, TypeError):
        return None


def plan_binding(original: bytes, cache: Path, windows: Path) -> tuple[bytes, dict]:
    """Preserve unrelated TOML semantics and reject restrictive/ambiguous policies."""
    text = original.decode("utf-8-sig")
    before = tomllib.loads(text)
    plugin = before.get("plugins", {}).get(PLUGIN, {})
    if plugin.get("enabled") is not True:
        raise ValueError("Enable the installed Rockwell plugin explicitly before binding its server.")
    bundled = plugin.get("mcp_servers", {}).get(SERVER, {})
    if set(bundled) - {"enabled"}:
        raise ValueError("Bundled tool policy requires explicit migration; no permissive route was created.")
    target = {"enabled": True, "command": str(windows / "System32/WindowsPowerShell/v1.0/powershell.exe"),
              "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(cache / "scripts/Start-Mcp.ps1")],
              "cwd": str(cache), "env": {"PSModulePath": ""}}
    direct = before.get("mcp_servers", {}).get(SERVER)
    if direct is not None and direct != target:
        raise ValueError("An existing direct Rockwell route differs; reconcile it before replacement.")
    expected = copy.deepcopy(before)
    expected["plugins"][PLUGIN].setdefault("mcp_servers", {}).setdefault(SERVER, {})["enabled"] = False
    expected.setdefault("mcp_servers", {})[SERVER] = target
    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.splitlines(keepends=True)
    path = ("plugins", PLUGIN, "mcp_servers", SERVER)
    headers = [(i, header_path(line.rstrip("\r\n"))) for i, line in enumerate(lines)]
    starts = [i for i, key in headers if key == path]
    if bundled and len(starts) != 1:
        raise ValueError("Bundled table syntax is not safely editable; no configuration changed.")
    if starts:
        start = starts[0]
        end = next((i for i, key in headers if i > start and key is not None), len(lines))
        positions = [i for i in range(start + 1, end) if re.match(r"^\s*enabled\s*=", lines[i])]
        if len(positions) > 1:
            raise ValueError("Ambiguous bundled enable flag.")
        if positions:
            i = positions[0]
            changed, count = re.subn(r"^(\s*enabled\s*=\s*)(true|false)(\s*(?:#[^\r\n]*)?)(\r?\n)?$",
                                     lambda m: m[1] + "false" + m[3] + (m[4] or ""), lines[i])
            if count != 1:
                raise ValueError("Bundled enable flag syntax is not safely editable.")
            lines[i] = changed
        else:
            lines.insert(start + 1, "enabled = false" + newline)
        text = "".join(lines)
    else:
        text += newline + f'[plugins."{PLUGIN}".mcp_servers."{SERVER}"]' + newline + "enabled = false" + newline
    if direct is None:
        # JSON basic strings are valid TOML basic strings for these fixed arguments/paths.
        text += newline + f'[mcp_servers."{SERVER}"]' + newline
        for key in ("enabled", "command", "args", "cwd"):
            text += key + " = " + json.dumps(target[key], ensure_ascii=False) + newline
        text += f'[mcp_servers."{SERVER}".env]' + newline + 'PSModulePath = ""' + newline
    if tomllib.loads(text) != expected:
        raise ValueError("Configuration semantic preservation failed; no configuration changed.")
    output = (b"\xef\xbb\xbf" if original.startswith(b"\xef\xbb\xbf") else b"") + text.encode("utf-8")
    return output, {"enabledRoutes": 1, "bundledEnabled": False, "direct": target,
                    "unrelatedConfigurationPreserved": True, "chatActivation": "not_observed"}


def apply_binding(config: Path, original: bytes, updated: bytes, backup: Path, expected: str) -> dict:
    if sha(original) != expected:
        raise ValueError("Reviewed configuration SHA-256 does not match; generate a fresh plan.")
    backup.mkdir(parents=True, exist_ok=False)
    (backup / "config.toml.before").write_bytes(original)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=config.parent, prefix=".rockwell-config-", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(updated)
            stream.flush()
            os.fsync(stream.fileno())
        if read(config) != original:
            raise ValueError("Configuration changed after review; backup retained and no update submitted.")
        os.replace(temporary, config)
        temporary = None
        if read(config) != updated:
            raise ValueError("Configuration readback differs; inspect retained evidence before any retry.")
        result = {"beforeSha256": expected, "afterSha256": sha(updated), "configurationReadback": "matched",
                  "reinstalled": False, "serviceRestarted": False, "chatActivation": "not_observed"}
        (backup / "binding-receipt.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        return result
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def serving(root: Path) -> dict:
    """Authenticated GETs only; no service launch, SCM access or native probing."""
    try:
        connection = load(root / ".local/connection.json")
        url = urlsplit(connection["url"])
        if url.scheme != "http" or not ipaddress.ip_address(url.hostname).is_loopback or url.username or url.password or url.path not in ("", "/") or url.query or url.fragment:
            raise ValueError("Invalid loopback service URL.")
        token_path = Path(connection["tokenFile"])
        if token_path.resolve().parent != (root / ".local").resolve():
            raise ValueError("Unexpected service credential location.")
        token = read(token_path, 4096).decode("utf-8-sig").strip()
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                raise ValueError("Service redirects are refused.")
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        def get(route):
            request = urllib.request.Request(connection["url"].rstrip("/") + route, headers={"Authorization": "Bearer " + token})
            with opener.open(request, timeout=3) as response:
                data = response.read(MAXIMUM_BYTES + 1)
            if len(data) > MAXIMUM_BYTES:
                raise ValueError("Service reply exceeds its size limit.")
            return json.loads(data)
        health, settings = get("/health"), get("/api/settings")
        if health.get("name") != "RockwellMCPServer":
            raise ValueError("Unexpected serving application.")
        options = settings["effective"]["Studio5000"]
        return {"state": "responding", "version": health["version"], "restartRequired": settings["restartRequired"],
                "effective": {**{key: options.get(key) for key in POLICY_KEYS},
                              "nativeBridgeConfigured": bool(options.get("NativeBridgePath")),
                              "cipBridgeConfigured": bool(options.get("CipBridgePath"))},
                "nativeAcceptance": "not_established_by_metadata", "controllerProbeAttempted": False}
    except Exception as error:
        return {"state": "unavailable", "errorType": type(error).__name__, "serviceLaunchAttempted": False}


def chat_visibility(evidence: dict | None) -> dict:
    if evidence is None:
        return {"state": "unknown", "reason": "Installed skills, CLI inventory and protocol smoke do not establish this chat's tools."}
    names = evidence.get("toolNames")
    if not isinstance(names, list) or len(names) > 4096 or any(not isinstance(n, str) or len(n) > 512 for n in names):
        raise ValueError("Supply bounded toolNames from the requesting chat's actual tool inventory.")
    visible = sorted(n for n in names if re.search(r"(^|__)rockwell_(broker_capabilities|read_workflow)$|(^|__)studio_extensions_catalog$", n))
    return {"state": "reported_present" if visible else "reported_missing", "evidence": "caller_supplied_chat_inventory",
            "chatId": evidence.get("chatId"), "recordedAt": evidence.get("recordedAt"), "discoveryTools": visible,
            "successfulChatCall": "not_observed_by_this_script"}


def resource_admission(root: Path, limit: int) -> dict:
    """Retained claims count toward admission, even when their processes have gone."""
    try:
        owner = load(root / ".local/service-owner.json")
        paths = list(itertools.islice((Path(owner["registry"]) / "resources").glob("*.json"), 4097))
        if len(paths) > 4096:
            raise ValueError("Resource history exceeds its bound.")
        records = [load(path) for path in paths]
        active = [r for r in records if r.get("app_id") == SERVER and r.get("state") not in ("released", "terminated", "failed")]
        return {"state": "at_limit" if len(active) >= limit else "capacity_available",
                "retainedNonterminalClaims": len(active), "configuredLimit": limit,
                "launchesWithoutProcessIdentity": sum(r.get("process") is None for r in active),
                "isLiveProcessCount": False, "cleanupAttempted": False,
                "action": "Preserve records. Reconcile only reviewed exact owner/child generations; do not raise limits or clear uncertain journals to bypass admission."}
    except Exception as error:
        return {"state": "unknown", "errorType": type(error).__name__, "cleanupAttempted": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("diagnose", "repair"))
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--codex-home", type=Path, default=Path(os.environ.get("CODEX_HOME", Path.home() / ".codex")))
    parser.add_argument("--visible-tools-json", type=Path)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--expected-config-sha256")
    parser.add_argument("--backup-directory", type=Path)
    args = parser.parse_args()
    root, home = args.root.resolve(), args.codex_home.resolve()
    config = home / "config.toml"
    original = read(config)
    cache = None
    try:
        plugin = inventory(home)
        cache, installed = cached_package(root, home, plugin)
        installed["state"] = "verified_cache_bytes"
    except Exception as error:
        installed = {"state": "unavailable", "errorType": type(error).__name__,
                     "reason": "Installed inventory/cache could not be verified; no connection change is permitted."}
    current = tomllib.loads(original.decode("utf-8-sig"))
    bundled = current.get("plugins", {}).get(PLUGIN, {}).get("mcp_servers", {}).get(SERVER, {})
    direct = current.get("mcp_servers", {}).get(SERVER)
    result = {"schemaVersion": 1, "configurationSha256": sha(original), "installed": installed,
              "sourceVersion": load(root / "release-version.json")["displayVersion"],
              "configured": {"pluginEnabled": current.get("plugins", {}).get(PLUGIN, {}).get("enabled"),
                             "bundledEnabled": bundled.get("enabled", True), "directPresent": direct is not None,
                             "directEnabled": direct.get("enabled", True) if direct is not None else False},
              "serving": serving(root), "nativeAcceptance": "requires_separate_operation_version_target_evidence",
              "chatVisible": chat_visibility(load(args.visible_tools_json) if args.visible_tools_json else None)}
    saved_options = load(root / "service.local.json").get("Studio5000", {})
    result["savedEngineeringPolicy"] = {key: saved_options.get(key) for key in POLICY_KEYS}
    result["resourceAdmission"] = resource_admission(root, saved_options.get("MaximumOwnedResources", 32))
    if args.mode == "repair":
        if cache is None:
            raise ValueError("Repair requires verified app-owned installed/cache evidence; inspect the diagnose report.")
        updated, plan = plan_binding(original, cache, Path(os.environ["SystemRoot"]))
        result["plan"] = plan
        if args.apply:
            if not args.expected_config_sha256 or not args.backup_directory:
                raise ValueError("Apply needs the reviewed configuration SHA-256 and a new private backup directory.")
            if (root / ".local/maintenance-hold.json").exists():
                raise ValueError("An operator maintenance hold blocks connection changes.")
            result["applied"] = apply_binding(config, original, updated, args.backup_directory, args.expected_config_sha256)
    elif args.apply:
        raise ValueError("Diagnose is read-only.")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
