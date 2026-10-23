# Optional app-owned framework host

[CodexMCPBroker](https://github.com/votecoffee/CodexMCPBroker) is the upstream repository for the shared application framework and a **separate Windows MCP Service Manager application**. Rockwell remains an independently usable MCP server with its own Codex plugin and skills. The manager is not a universal plugin or an MCP front door. This guide describes Rockwell's reviewed pinned integration, not automatic adoption of the upstream repository's newest release.

The `rockwell-framework-host` Python package adds an experimental, app-owned host around the existing C# MCP adapter. Its source pins **universal-codex-tools 0.20.0 / MCP Python SDK 2.3.0**, from [CodexMCPBroker commit 5b2cac5](https://github.com/votecoffee/CodexMCPBroker/tree/5b2cac50ca24d21a15b732e0424bb02fd794e1fb). The C# adapter keeps its separately locked **ModelContextProtocol 2.2.0** dependency. Python and .NET SDK versions are independent; no .NET framework binding is provided upstream.

The existing standalone Codex plugin, 59-tool adapter, GUI and REST service remain available. The optional host exposes the framework's fourteen fixed discovery/invocation tools and discovers the C# adapter behind them. All project state, review hashes, target authorization, concurrent transactions and native jobs remain in the shared .NET service.

The architecture is a shared app framework plus an independent Windows manager. Each app owns and installs its own Codex plugin and native skills. Other installed plugins do not change Rockwell's integration selection. A universal plugin, aggregated native-skill package and universal Codex MCP connection are outside this deployment architecture. The retained Python distribution/import identifiers `universal-codex-tools` / `universal_tools` provide package compatibility only. Internal routing-library code and protocol fixtures do not establish a plugin installation route.

The 0.20.0 source pin adds the reviewed [resource ledger and contained background launcher](https://github.com/votecoffee/CodexMCPBroker/blob/5b2cac50ca24d21a15b732e0424bb02fd794e1fb/docs/resource-ownership.md). Package/RECORD checks and pure tests are separate from app-host, installed plugin and native acceptance. Previously accepted runtime profiles used 0.18.0; their results do not establish runtime acceptance of this upgrade. Engineering hosts and plugin selection are not changed by preparing an isolated library-review environment.

This alignment preserves the assessed legacy `2025-11-25` wire profile, transient adapters, disabled discovery caching and explicit refresh. Publication pins structural argument budgets to 64 levels and 100,000 nodes, alongside the existing 8 MiB byte budget. Verification rejects unassessed protocol/cache/budget edits. Both the standalone plugin bootstrap and optional backend use the surviving .NET `Studio5000.StdioHost` owner with a pinned adapter closure and raw stdio. No broker plugin installation route is added.

Framework 0.20 provides optional [Tasks, Skills and lifecycle APIs](https://github.com/votecoffee/CodexMCPBroker/blob/5b2cac50ca24d21a15b732e0424bb02fd794e1fb/docs/extension-development.md). The explicit `--skills` profile enables app-owned guidance publication on compatible endpoints. Tasks and modern native job relays remain blocked by the concrete gaps in the [feature report](PROTOCOL_FEATURES.md). Native SDK jobs, ownership, approvals, revision checks and uncertain outcomes keep their existing app contracts; no generic call becomes a Task.

The optional [remote OAuth, protocol relay, Apps and Redis deployment profiles](https://github.com/votecoffee/CodexMCPBroker/blob/5b2cac50ca24d21a15b732e0424bb02fd794e1fb/docs/deployment.md) are outside this local integration. The host retains one owning process and loopback authentication. No remote endpoint, replicated worker, Redis extra or UI extension is enabled by installation. Native jobs and engineering sessions remain in the shared Rockwell service, with their existing receipts and recovery barriers.

The framework MCP front door is optional. Normal installation uses the standalone plugin; `Run.cmd` starts the shared service and GUI. Preparing an environment alone does not install plugins, register services, change runtime pointers or connect to a controller. The explicitly selected [management supervisor](SERVICE_MANAGEMENT.md) uses this same compatible runtime without changing the MCP front door. Moving a client to the optional framework MCP route still requires separate acceptance and explicit configuration.

## Dependency and package preparation

Use a fresh app-owned Python environment. Windows with Python 3.13 is the upstream reference profile; other interpreter/platform combinations need their own validation. The exact reviewed wheel name, SHA-256, source revision and SDK versions are recorded in [baseline.json](../framework/src/rockwell_framework/baseline.json). Runtime dependencies are pinned in [requirements-runtime-lock.txt](../framework/requirements-runtime-lock.txt). Framework code is a package dependency; it is neither copied into this repository nor imported from another checkout's current branch.

Obtain the reviewed framework wheel from the framework release maintainer. This checkpoint does not imply a public package-feed release. Rebuilding a wheel can change its archive hash; review and explicitly update the baseline before using a different artifact, even with the same version number.

```powershell
.\scripts\Initialize-Framework.ps1 -FrameworkWheel 'C:\Packages\universal_codex_tools-0.20.0-py3-none-any.whl' -RuntimeDirectory .local/framework-runtime-0.20.0
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

## Current guidance and Skills-over-MCP

The selected standalone .NET endpoint keeps its 59 ordinary tools. It has no configured Skills extension handlers. Its packaged `skills/` files are small bootstrap instructions that fetch the full revision-bound guides and references from shared Core, whose only maintained sources are in `workflows/`.

For a separately configured, compatible app-owned Python endpoint, explicitly opt in:

```powershell
& $python -m rockwell_framework --state .local/framework-host serve --skills
& $python -m rockwell_framework --state .local/framework-host frontend --skills
```

Each endpoint publishes its own real three-skill catalog; enable the flag on the endpoint the client actually uses. It does not automatically proxy the other endpoint's extensions or switch the installed standalone plugin. A running backend with `skill_source` support is required; legacy metadata fails clearly instead of publishing an empty catalog. The app's existing loopback credential authenticates metadata reads. No caller URL, filesystem path or engineering target is accepted.

The `SkillCatalog`/`ExtensionServer` profile supports `skills/list`, `skills/get` and ordinary `resources/read`, with exact manifest sizes/hashes and the original distinct names/descriptions. Directory reads are disabled. It snapshots immutable canonical bytes, validates the narrower OpenAI importer budgets, and executes no contributed code. No native skill discovery folder is written.

Two metadata tools supplement the fourteen ordinary framework tools:

| Tool | Contract |
|---|---|
| `rockwell_protocol_doctor` | Read the endpoint's current catalog revision/generation, source bindings and feature gaps. No engineering I/O. |
| `rockwell_refresh_guidance(expectedRevision)` | Read current app metadata and explicitly publish a fully validated replacement with `catalog.publish(..., expected_revision=...)`. Metadata mutation only; no engineering approval, operation or route change. |

Unchanged workflow revisions reuse the endpoint's immutable bytes after one fresh capability read. A changed guide fetches its full pinned guide/reference set and checks the source workflow bindings again before publication. No project data, approvals or invocation results are cached. Concurrent stale publication, malformed content, hash mismatches and mid-fetch source changes retain the prior catalog. A changed generation rejects old pagination cursors. Resource-list/generation notifications are hints; reconnecting clients refetch and verify current manifests rather than assuming an event history.

Cancellation during notification delivery can follow a committed metadata publication; it does not undo the new catalog. Its source bindings remain attached, and a caller with a lost reply reads the doctor before another refresh. While publication is in progress, the doctor marks source bindings temporarily unknown rather than combining different generations. This metadata behavior does not bind MCP cancellation to native engineering jobs.

Tests demonstrate updates/additions/removals through one already connected modern client and isolation between catalog instances. The generated three-guide/three-reference benchmark reduces unchanged-refresh metadata round trips from eight to one; this is synthetic metadata evidence, not production engineering latency. Detailed guides remain embedded in Core, so updating them first requires the normal shared-service deployment. Editing source Markdown alone changes no running endpoint.

OpenAI currently imports MCP skills as submission-time snapshots; scanning/submitting a plugin update and native host activation remain separate from live publication. See [official importer guidance](https://developers.openai.com/plugins/build/skills#import-a-skill-from-mcp). Neither these protocol checks nor package installation establishes native Codex skill selection or controller acceptance.

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
& $python scripts/verify_guidance.py --workspace-root <isolated-profile>
```

The framework profile rejects controller-enabled profiles, configured targets and SDK/CIP bridges. It starts only its own offline service/host/stdio children, uses the installed wheel, and retains private evidence under ignored paths. It compares all actual C# tool definitions and complete selected results, checks both connected frontends through deployment replacement, preserves workflow contents, rejects stale revisions and unauthorized write channels, exercises Core preview/apply and durable receipts, and verifies the framework's app-error reconciliation barrier. Pure tests cover publication defaults, full dependency tampering, routing, competing publishers and link rejection.

Raw selected results are retained with wire aliases and unset model fields excluded. Result comparison normalizes only the optional success default: the C# SDK omits `isError: false`, while the Python server emits it. Content, structured data, metadata and true error flags remain part of the comparison.

The assessed wire contract is the negotiated `2025-11-25` protocol, with SDK 2 snake_case Python attributes, typed pagination and `by_alias=True` wire serialization. This profile does not exercise app-origin forms/progress, modern SDK discovery/cache/input-required behavior, native projects/devices, Codex activation, performance or UI acceptance. The library's own synthetic tests are separate evidence. The app-host profile uses the installed framework package and the actual C# adapter; it does not import framework implementation from a source checkout. See [upstream framework boundaries](https://github.com/votecoffee/CodexMCPBroker/blob/5b2cac50ca24d21a15b732e0424bb02fd794e1fb/docs/app-framework.md) and [validation](VALIDATION.md).
