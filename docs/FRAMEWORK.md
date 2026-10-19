# Optional app-owned framework host

The `rockwell-framework-host` Python package adds an experimental, app-owned host around the existing C# MCP adapter. It uses **universal-codex-tools 0.14.0 / MCP Python SDK 2.3.0**, from [CodexMCPBroker commit 52478c3](https://github.com/votecoffee/CodexMCPBroker/tree/52478c3868b18faf821ca22fd2bc38e5d0599b9b). The C# adapter keeps its separately locked **ModelContextProtocol 2.2.0** dependency. Python and .NET SDK versions are independent; no .NET framework binding is provided upstream.

The existing standalone Codex plugin, 59-tool adapter, GUI and REST service remain available. The optional host exposes the framework's fourteen fixed discovery/invocation tools and discovers the C# adapter behind them. All project state, review hashes, target authorization, concurrent transactions and native jobs remain in the shared .NET service. A universal broker is not required to run this host.

This is an optional source/package integration. Normal installation and `Run.cmd` retain their existing route. Preparing this environment does not install plugins, register services, change installed runtime pointers or connect to a controller. Moving an installed client to this front door requires separate acceptance and explicit configuration. Optional aggregation of this framework host through another broker has not been assessed.

## Dependency and package preparation

Use a fresh app-owned Python environment. Windows with Python 3.13 is the upstream reference profile; other interpreter/platform combinations need their own validation. The exact reviewed wheel name, SHA-256, source revision and SDK versions are recorded in [baseline.json](../framework/src/rockwell_framework/baseline.json). Runtime dependencies are pinned in [requirements-runtime-lock.txt](../framework/requirements-runtime-lock.txt). Framework code is a package dependency; it is neither copied into this repository nor imported from another checkout's current branch.

Obtain the reviewed framework wheel from the framework release maintainer. This checkpoint does not imply a public package-feed release. Rebuilding a wheel can change its archive hash; review and explicitly update the baseline before using a different artifact, even with the same version number.

```powershell
.\scripts\Initialize-Framework.ps1 -FrameworkWheel 'C:\Packages\universal_codex_tools-0.14.0-py3-none-any.whl'
```

The script checks the wheel hash, creates `.local/framework-runtime`, installs the pinned dependencies and this app's package, then runs `pip check`. It refuses to replace an existing environment. Select a new `-RuntimeDirectory` for an upgrade and retire the old host after its app work has been reconciled. Do not install dependencies during live tool calls. A specific Python executable can be selected with `-Python`.

For a distributable app-host wheel, build `framework/` with the normal Python build tool and keep output outside source:

```powershell
$python = (Resolve-Path .local/framework-runtime/Scripts/python.exe).Path
& $python -m pip install build==1.6.1
& $python -m build --wheel --outdir artifacts/framework/wheels framework
```

Install that app wheel with `--no-deps` into an environment already prepared with the pinned runtime dependencies. The wheel includes the baseline and MIT license; machine-specific publication files and credentials remain outside it.

## Publish, serve and connect

Publish a complete built `artifacts/app/Mcp` directory into a new dedicated framework state:

```powershell
& $python -m rockwell_framework --state .local/framework-host publish --app-root . --port 8891
& $python -m rockwell_framework --state .local/framework-host verify
```

Publication copies every backend file into a content-addressed deployment, verifies its full inventory, and creates an application definition with a pinned executable hash. Existing source files and deployed revisions are preserved. Keep the selected state path short: native Windows deployment paths over 240 characters are rejected before copying. `--backend-root` selects a separately published adapter build. No service or native session starts during publication.

The default framework policy is read-only. Explicit administrative publication can enable exact outer tools using repeated `--allow-write-tool` arguments. For example, an offline preview/apply workflow requires `studio_register_project`, `studio_preview_changes` and `studio_apply_changes`. The framework allowlist does not grant app authorization, enable controller operations or replace generation/target/review arguments. It cannot restrict the inner operations of one outer extension tool; app effect channels remain authoritative.

Start the app's ordinary shared service separately. In another terminal, run the optional host:

```powershell
& $python -m rockwell_framework --state .local/framework-host serve
```

It authenticates loopback HTTP using credentials in its dedicated state. Each frontend process connects to this already running app host:

```powershell
& $python -m rockwell_framework --state .local/framework-host frontend
```

The frontend's stdout is MCP only. It uses the packaged `RemoteBackend` and `create_surface`, giving each process independent affinity and the framework's request-bound routing. It never starts the universal daemon or the app host automatically. The existing standalone plugin continues to call the C# adapter directly; these commands do not edit its manifest or install a different plugin.

## State, publication and recovery

Framework state cannot reuse a universal broker's registrations, another app root or the service's workspace state. A local OS lock serializes publishers. The backend has a stable resource ID derived from the explicit app root, with `session_mode: transient`: each adapter connection is temporary while projects, jobs, transactions and durable apply receipts belong to the independent service. There is no implicit per-chat selected document or controller in this adapter. Calls through one framework backend remain serialized; this profile does not claim throughput or latency parity.

Publish a new deployment, then explicitly call `refresh_services` through the still-connected frontend. The framework validates publication revisions and entrypoint bytes; previous catalog revisions cannot dispatch after replacement. Frequently changing extension schemas and workflow guides still use the existing Rockwell read tools and generation checks. Changing Core/native engines, framework dependencies or native skills retains its normal update/restart lifecycle.

The app publisher hashes the complete copied deployment and checks it again on `verify` and host startup. The framework additionally checks the pinned executable before metadata probing and dispatch. Keep deployed directories immutable; the entrypoint guard alone does not authenticate all dependencies or native resources.

Write request IDs are durably claimed by the framework. Inspect `get_request_status` and the app's jobs/audit/current revision after a failure; do not dispatch another start/apply to discover its outcome. App-error, interrupted and uncertain receipts can defer backend replacement until app-owned reconciliation. The upstream local app-admin acknowledgment API requires the current receipt after that verification. This package publishes no model-facing recovery, environment installation or executable-path tool. Restarting, changing routes or renaming an adapter is not outcome reconciliation.

## Isolated validation and limits

```powershell
& $python -m pip install pytest==9.1.1
& $python -m pytest -q framework/tests
.\scripts\Verify-Isolated.ps1
& $python scripts/verify_framework.py --workspace-root <isolated-profile>
& $python scripts/verify_broker.py --broker-root C:\Tools\CodexMCPBroker --workspace-root <isolated-profile>
```

The framework profile rejects controller-enabled profiles, configured targets and SDK/CIP bridges. It starts only its own offline service/host/stdio children, uses the installed wheel, and retains private evidence under ignored paths. It compares all actual C# tool definitions and complete selected results, checks both connected frontends through deployment replacement, preserves workflow contents, rejects stale revisions and unauthorized write channels, exercises Core preview/apply and durable receipts, and verifies the framework's app-error reconciliation barrier. Pure tests cover publication defaults, full dependency tampering, routing, competing publishers and link rejection.

Raw selected results are retained with wire aliases and unset model fields excluded. Result comparison normalizes only the optional success default: the C# SDK omits `isError: false`, while the Python server emits it. Content, structured data, metadata and true error flags remain part of the comparison.

The assessed wire contract is the negotiated `2025-11-25` protocol, with SDK 2 snake_case Python attributes, typed pagination and `by_alias=True` wire serialization. This profile does not exercise app-origin forms/progress, modern SDK discovery/cache/input-required behavior, native projects/devices, Codex activation, performance or UI acceptance. The library's own synthetic tests are separate evidence. The direct broker profile uses the installed framework package and the actual C# adapter; it does not import framework implementation from a source checkout. See [upstream framework boundaries](https://github.com/votecoffee/CodexMCPBroker/blob/52478c3868b18faf821ca22fd2bc38e5d0599b9b/docs/app-framework.md) and [validation](VALIDATION.md).
