# Service manager reporting

Rockwell uses the shared `universal-codex-tools==0.20.0` management interface through an app-owned Python supervisor for its existing .NET service. The standalone plugin still connects to the .NET MCP adapter. The optional framework MCP front door remains optional. No universal broker, additional manager instance, Tasks, Skills API, OAuth, Apps UI or Redis profile is activated.

The Windows MCP Service Manager is a separate shared application. This app does not install or start another manager. Follow the framework's [manager documentation](https://github.com/votecoffee/CodexMCPBroker/blob/5b2cac50ca24d21a15b732e0424bb02fd794e1fb/docs/service-management.md) if a manager is needed.

Each app keeps its own Codex plugin and native skills. The shared framework and independent manager do not aggregate plugins or create a universal Codex MCP connection. The Python package/import names remain `universal-codex-tools` / `universal_tools` for compatibility. This app's exact reviewed package pin remains authoritative until an app-owned migration is reviewed and validated; architecture announcements do not install a release or supply the pending durable resource-ledger API.

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

The installer records the actual installed canonical plugin ID and version plus the built application version/Core hash. The selected runtime pointer and manager registration are private local state. Runtime setup verifies wheel SHA-256, RECORD/package bytes and dependency consistency; it does not install packages during live requests.

## Status and restart boundaries

The supervisor owns one .NET child and one `ManagedService` registration with a fresh random instance ID. Its fast, thread-safe status callback returns a cached snapshot. A separate loop polls only the authenticated `/api/management/activity` endpoint and bounded installation metadata. The endpoint reads in-memory workspace/native-job/watch/extension counts without attaching projects, enumerating retained plans, starting workers or contacting controllers. Activity counts are conservative; they are not a restart approval.

Source lifecycle handling now observes the supervisor's pinned process identity from its marked child and closes that invocation's child on controlled exit or failure after launch. This is separate from the forthcoming durable resource ledger and has no abrupt native-tree cleanup acceptance yet. See [process ownership](PROCESS_LIFETIMES.md).

Version rows distinguish installed, running and locally available app/plugin/framework/SDK versions and explicit `update_pending`. Plugin versions loaded by individual chats are unknown. Availability is local installation/source metadata, not a package-feed or GitHub query; unknown values stay unknown. Reporting failures mark the row degraded and cannot terminate the serving child.

**Tray restart is disabled in this release.** Rockwell does not yet atomically fence every chat's work, unsaved GUI documents, prepared reviews, queued jobs and unresolved native effects. The status provides that specific reason. The restart callback also refuses every request as defense in depth. No tray action schedules, retries or rolls back a native operation or service restart. Manual operator stop/update scripts retain their separate ownership and quiescence requirements.

The framework provides authenticated literal-loopback management, Host/Origin checks, private per-user registry ACLs, bounded payloads and a durable app-scoped at-most-once SQLite restart journal. Identical requests return retained receipts; changed payloads reject; uncertain intents block subsequent restarts across instances. The app does not clear these barriers automatically or expose reconciliation through HTTP/MCP. Synthetic tests use app-only `pending_restarts()`/`reconcile_restart()` after independently verifying a zero-effect outcome. A stopped/unreachable supervisor cannot be restarted by the manager; no PID guessing or launch-command discovery is added.

The default registry uses the Windows user's Local AppData known folder without package redirection, followed by `CodexMCPManager\Registry`. An explicit dedicated registry is supported for isolation tests. Tokens/endpoints remain private and are omitted from manager snapshots. Clean operator shutdown retains a stopped registration and does not erase the journal.

## Verification

Run `python -m pytest -q framework/tests` in the installed app-owned runtime. Management fixtures cover cached status, unknown/pending versions, authentication, credential-free snapshots, stale/busy denial, concurrent duplicates with independent zero-effect counts, durable uncertain barriers and clean shutdown. Upstream management tests use only synthetic workers for accepted-restart effect counts; they do not prove a real Rockwell restart.

`scripts/verify_management.py --workspace-root <isolated-profile>` exercises the normal launch/stop scripts, owning supervisor, actual isolated .NET service, manager row and real authenticated restart refusal. It rejects profiles with controllers or native SDK/CIP bridges enabled. `scripts/verify_framework.py` separately checks existing tool/schema parity through the installed optional framework host. Actual live restart acceptance remains unavailable until Rockwell implements and validates its full restart fence.
