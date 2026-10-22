# Service manager reporting

Rockwell uses the shared `universal-codex-tools==0.20.0` management interface through an app-owned Python supervisor for its existing .NET service. The standalone plugin still connects to the .NET MCP adapter. The optional framework MCP front door remains optional. No universal broker, additional manager instance, Tasks, Skills API, OAuth, Apps UI or Redis profile is activated.

The Windows MCP Service Manager is a separate shared application. This app does not install or start another manager. Follow the framework's [manager documentation](https://github.com/votecoffee/CodexMCPBroker/blob/5b2cac50ca24d21a15b732e0424bb02fd794e1fb/docs/service-management.md) if a manager is needed.

Each app keeps its own Codex plugin and native skills. The shared framework and independent manager do not aggregate plugins or create a universal Codex MCP connection. The Python package/import names remain `universal-codex-tools` / `universal_tools` for compatibility. This app's exact reviewed package pin remains authoritative; preparing an isolated review environment does not migrate a live registration or install/enable a plugin.

## Enable an app-owned runtime

Prepare a new compatible environment using the exact reviewed wheel in `framework/src/rockwell_framework/baseline.json`:

```powershell
.\scripts\Initialize-Framework.ps1 -FrameworkWheel 'C:\Packages\universal_codex_tools-0.20.0-py3-none-any.whl' -RuntimeDirectory .local/framework-runtime-0.20.0
```

After the application has been built and installed, select that verified runtime:

```powershell
.\scripts\Enable-Management.ps1 -RuntimeDirectory .local/framework-runtime-0.20.0 -FrameworkWheel 'C:\Packages\universal_codex_tools-0.20.0-py3-none-any.whl'
```

Selection affects the next ordinary service start. Finish active work before stopping the existing service. `Run.cmd`, the GUI shortcut and `Start-Service.ps1` use the selected supervisor; without a selected runtime, direct service startup remains supported. Dependency or provenance failures fail explicitly rather than silently switching runtimes. The old environment and runtime state are preserved. Python MCP SDK remains 2.3.0; the .NET adapter has its independently locked 2.2.0 SDK.

The supervisor source uses `owned-service-v2`, preserving native SDK/CIP features and existing controller authorization. Before business services initialize, Core requires the exact parent/service identity and the supervisor's durable shutdown barrier. The barrier clears only after matching native/resource close evidence and observed service exit. The earlier offline-only generation is not adopted. `Enable-Management.ps1` refuses runtime-pointer mutation while the operator maintenance marker is present. Do not select/install this source profile during a runtime hold.

The installer records the actual installed canonical plugin ID and version plus the built application version/Core hash. The selected runtime pointer and manager registration are private local state. Runtime setup verifies wheel SHA-256, RECORD/package bytes and dependency consistency; it does not install packages during live requests.

## Status and restart boundaries

The supervisor owns one .NET child and one `ManagedService` registration with a fresh random instance ID. Its fast, thread-safe status callback returns a cached snapshot. A separate loop polls only the authenticated `/api/management/activity` endpoint and bounded installation metadata. The endpoint reads in-memory workspace/native-job/watch/extension counts without attaching projects, enumerating retained plans, starting workers or contacting controllers. Activity counts are conservative; they are not a restart approval.

The supervisor owns the service tree through the framework's durable `ResourceLedger`/`ResourceOwner` API and suspended Windows job launch. Its generation, exact process identity, member snapshots and cleanup receipt survive manager endpoint loss. It observes a pinned service handle rather than reopening PIDs. The child also observes its supervisor. Refusal, unverified exit and reporting/IPC failures retain the owner and native barrier. Controlled verified exit closes only the recorded tree; uncertain cleanup remains retained. Harmless process fixtures have passed; actual service/native acceptance is separate. See [process ownership](PROCESS_LIFETIMES.md).

Both managed and direct `Stop-Service.ps1` paths authenticate private generation-bound stop intents and observe owned cleanup receipts. Neither terminates a saved PID. Same-generation pending intents keep their ID; uncertain/terminal records require inspection. Direct startup publishes its own full identity and uses the same service-owned worker/native ledger. Stub fixtures cover both stop branches across PowerShell versions. Actual application acceptance remains a controlled runtime check.

Version rows distinguish installed, running and locally available app/plugin/framework/SDK versions and explicit `update_pending`. Plugin versions loaded by individual chats are unknown. Availability is local installation/source metadata, not a package-feed or GitHub query; unknown values stay unknown. Reporting failures mark the row degraded and cannot terminate the serving child.

**Tray restart is disabled in this release.** Rockwell does not yet atomically fence every chat's work, unsaved GUI documents, prepared reviews, queued jobs and unresolved native effects. The status provides that specific reason. The restart callback also refuses every request as defense in depth. No tray action schedules, retries or rolls back a native operation or service restart. Manual operator stop/update scripts retain their separate ownership and quiescence requirements.

The framework provides authenticated literal-loopback management, Host/Origin checks, private per-user registry ACLs, bounded payloads and a durable app-scoped at-most-once SQLite restart journal. Identical requests return retained receipts; changed payloads reject; uncertain intents block subsequent restarts across instances. The app does not clear these barriers automatically or expose reconciliation through HTTP/MCP. Synthetic tests use app-only `pending_restarts()`/`reconcile_restart()` after independently verifying a zero-effect outcome. A stopped/unreachable supervisor cannot be restarted by the manager; no PID guessing or launch-command discovery is added.

The default registry uses the Windows user's Local AppData known folder without package redirection, followed by `CodexMCPManager\Registry`. An explicit dedicated registry is supported for isolation tests. Tokens/endpoints remain private and are omitted from manager snapshots. Clean operator shutdown retains a stopped registration and does not erase the journal.

## Verification

Run `python -m pytest -q framework/tests` in the installed app-owned runtime. Management fixtures cover cached status, unknown/pending versions, authentication, credential-free snapshots, stale/busy denial, concurrent duplicates with independent zero-effect counts, durable uncertain barriers and clean shutdown. Upstream management tests use only synthetic workers for accepted-restart effect counts; they do not prove a real Rockwell restart.

During a runtime hold, select only the documented pure file/stub tests. The separate opt-in harmless Python resource fixtures start no engineering server. Package/provenance and these fixtures do not establish app-host, MCP EOF, native resource or installed acceptance of framework 0.20.

`scripts/verify_management.py --workspace-root <isolated-profile>` exercises the normal launch/stop scripts, owning supervisor, actual isolated .NET service, manager row and real authenticated restart refusal. It rejects profiles with controllers or native SDK/CIP bridges enabled. `scripts/verify_framework.py` separately checks existing tool/schema parity through the installed optional framework host. Actual live restart acceptance remains unavailable until Rockwell implements and validates its full restart fence.
