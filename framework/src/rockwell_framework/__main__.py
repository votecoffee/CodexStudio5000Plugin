"""Explicit app administration and a per-frontend stdio bridge; no automatic startup."""

import argparse
import json
import os
from pathlib import Path

from universal_tools.framework import serve_application
from universal_tools.models import load_settings
from universal_tools.surface import RemoteBackend, create_surface

from .guidance import AppGuidanceCatalog, GuidanceSource
from .publication import INSTRUCTIONS, check_versions, profile, publish, verify


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state", type=Path, required=True)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare = commands.add_parser(
        "publish", help="snapshot an adapter without connecting or starting a host"
    )
    prepare.add_argument("--app-root", type=Path, required=True)
    prepare.add_argument("--backend-root", type=Path)
    prepare.add_argument("--port", type=int)
    prepare.add_argument("--allow-write-tool", action="append", default=[])
    commands.add_parser("verify", help="check dependency and complete deployment inventory")
    serve = commands.add_parser(
        "serve", help="start this app's authenticated host, never the universal daemon"
    )
    serve.add_argument(
        "--skills",
        action="store_true",
        help="publish canonical app guidance through Skills-over-MCP",
    )
    frontend = commands.add_parser(
        "frontend", help="connect stdio to an already running app host; no auto-start"
    )
    frontend.add_argument(
        "--skills",
        action="store_true",
        help="publish guidance on this stdio endpoint too; no route switch",
    )
    args = parser.parse_args()
    check_versions()
    if args.command == "publish":
        result = publish(
            args.app_root,
            args.state,
            backend_root=args.backend_root,
            port=args.port,
            allowed_write_tools=args.allow_write_tool,
        )
        print(
            json.dumps(
                {
                    "published": True,
                    "revision": result["backends"][0]["revision"],
                    "host_started": False,
                    "native_connection_attempted": False,
                }
            )
        )
    elif args.command == "verify":
        verify(args.state)
        print(json.dumps({"verified": True, "native_connection_attempted": False}))
    elif args.command == "serve":
        marker = verify(args.state)
        if (Path(marker["app_root"]) / ".local/maintenance-hold.json").exists():
            raise ValueError("Operator maintenance hold requires explicit human release before startup")
        os.environ["STUDIO5000_PLUGIN_ROOT"] = marker["app_root"]
        skills = AppGuidanceCatalog(GuidanceSource(marker["app_root"])) if args.skills else None
        serve_application(args.state, args.state / "application.json", skills=skills)
    else:
        marker = profile(args.state)
        if (Path(marker["app_root"]) / ".local/maintenance-hold.json").exists():
            raise ValueError("Operator maintenance hold requires explicit human release before connecting")
        skills = AppGuidanceCatalog(GuidanceSource(marker["app_root"])) if args.skills else None
        create_surface(
            RemoteBackend(load_settings(args.state)),
            name="RockwellMCPServer",
            instructions=INSTRUCTIONS,
            skills=skills,
        ).run(transport="stdio")


if __name__ == "__main__":
    main()
