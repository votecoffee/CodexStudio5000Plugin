"""Owned supervisor/native-service lifecycle using an isolated, controller-disabled fixture."""

import argparse
import hashlib
import json
import os
import secrets
import shutil
import subprocess
import sys
import time
from pathlib import Path

from rockwell_framework import __version__
from rockwell_framework.management import RESTART_REASON
from universal_tools.management import ManagementClient


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace-root", type=Path, required=True)
    args = parser.parse_args()
    root = args.workspace_root.resolve()
    config = json.loads((root / "service.local.json").read_text("utf-8-sig"))[
        "Studio5000"
    ]
    if (
        config.get("EnableControllerOperations")
        or config.get("ControllerTargets")
        or config.get("NativeBridgePath")
        or config.get("CipBridgePath")
    ):
        raise ValueError(
            "Management checks require an isolated controller-disabled fixture"
        )
    local = root / ".local"
    local.mkdir(exist_ok=True)
    registry = local / "management-test-registry"
    (local / "management-installation.json").write_text(
        json.dumps(
            {
                "schemaVersion": 1,
                "appVersion": __version__,
                "pluginVersion": __version__,
                "pluginId": "rockwell-mcp-server@synthetic",
                "mcpDotnetSdk": "2.2.0",
            }
        )
    )
    (local / "management-runtime.json").write_text(
        json.dumps(
            {
                "schemaVersion": 1,
                "python": sys.executable,
                "pythonSha256": hashlib.sha256(
                    Path(sys.executable).read_bytes()
                ).hexdigest(),
                "frameworkVersion": "0.18.0",
                "mcpPythonSdk": "2.3.0",
                "registry": str(registry),
            }
        )
    )
    (root / "plugin.json").write_text(json.dumps({"version": __version__}))
    repo = Path(__file__).resolve().parent.parent
    for name in ("Start-Service.ps1", "Stop-Service.ps1"):
        shutil.copyfile(repo / "scripts" / name, root / "scripts" / name)
    environment = dict(os.environ)
    environment.pop("STUDIO5000_PLUGIN_ROOT", None)
    # Let Windows PowerShell construct its own module paths, independent of a PS7 parent.
    for key in list(environment):
        if key.upper() == "PSMODULEPATH":
            del environment[key]
    client = ManagementClient(registry)
    checks = []

    def check(condition, label):
        if not condition:
            raise AssertionError(label)
        checks.append(label)

    try:
        with (local / "start-script.log").open("wb") as log:
            subprocess.run(
                [
                    "powershell.exe",
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(root / "scripts/Start-Service.ps1"),
                    "-Background",
                ],
                env=environment,
                check=True,
                stdout=log,
                stderr=subprocess.STDOUT,
                timeout=45,
            )
        deadline = time.monotonic() + 15
        row = None
        while time.monotonic() < deadline:
            rows = client.snapshot()["services"]
            row = next((r for r in rows if r["status"]["state"] == "running"), None)
            if row and row["status"]["state"] == "running":
                break
            time.sleep(0.25)
        check(
            row is not None and row["status"]["state"] == "running",
            "Owned supervisor reports its live shared service",
        )
        check(
            row["plugin_id"] == "rockwell-mcp-server@synthetic",
            "Canonical fixture plugin identity is retained",
        )
        status = row["status"]
        check(
            status["versions"]["app"]["running"] == __version__,
            "Actual .NET running version is observed through metadata only",
        )
        check(
            status["versions"]["framework"]["running"] == "0.18.0"
            and status["versions"]["mcp-python"]["running"] == "2.3.0",
            "Compatible installed framework and SDK versions are reported",
        )
        check(
            not status["restart_allowed"]
            and status["restart_reason"] == RESTART_REASON,
            "Live service restart remains disabled with the app-owned reason",
        )
        before_pid = (local / "service.pid").read_text().strip()
        result = client.restart(
            row["app_id"],
            row["instance_id"],
            row["revision"],
            secrets.token_hex(16),
        )
        check(
            result["outcome"] == "blocked"
            and (local / "service.pid").read_text().strip() == before_pid,
            "Real authenticated restart denial has no process replacement effect",
        )
        check(
            "token" not in json.dumps(row) and "endpoint" not in json.dumps(row),
            "Manager snapshots exclude private control credentials",
        )
    finally:
        with (local / "stop-script.log").open("wb") as log:
            subprocess.run(
                [
                    "powershell.exe",
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(root / "scripts/Stop-Service.ps1"),
                ],
                env=environment,
                check=True,
                stdout=log,
                stderr=subprocess.STDOUT,
                timeout=15,
            )
    rows = client.snapshot()["services"]
    check(
        any(
            r["instance_id"] == row["instance_id"] and r["status"]["state"] == "stopped"
            for r in rows
        ),
        "Operator stop closes the supervisor and retains a stopped registration",
    )
    output = root / "artifacts/test-results/management-integration.json"
    output.write_text(
        json.dumps(
            {
                "passed": len(checks),
                "checks": checks,
                "liveRestartAccepted": False,
                "nativeControllerConnectionAttempted": False,
            },
            indent=2,
        )
    )
    print(json.dumps({"passed": len(checks), "output": str(output)}))


if __name__ == "__main__":
    main()
