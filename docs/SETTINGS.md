# Service settings and local access

Open **Settings…** in the main desktop workspace. The GUI edits the shared service's `service.local.json`; the same validation and save operations are available through authenticated REST. It never connects to a controller to read or save settings.

## Editable policy

| Control | Meaning |
| --- | --- |
| Enable controller operations | Allows controller-capable jobs and online polling when an explicit target/action is selected. Defaults to off. |
| Allow any explicit SDK communications path | Accepts `controllerTargetId: "path:<communications path>"` in addition to named targets. Defaults to off; no target discovery is performed. |
| Allow any source path | Accepts explicit absolute input paths outside `AllowedRoots`, subject to Windows access and normal input validation. Defaults to off. |
| Allow any output path | Allows new exports outside `OutputRoots`, subject to Windows access and normal export checks. Defaults to off. |
| Controller targets | Add/edit/remove IDs, SDK communications paths, optional exact method/program/routine restrictions and optional CIP gateway/route/timeout settings. |
| Runtime and limits | Edit the loopback URL, state directory, SDK/CIP worker paths, FactoryTalk schema/Studio paths, View Designer path, extension paths/timeouts and concurrency limits. |
| SDK service recovery | `EnableSdkServiceRecovery` and `AutoStartSdkService` default to true. Allow starting stopped `LdSdkService` and independently enable preflight before new SDK work. `SdkServiceStartTimeoutSeconds` defaults to 30, accepts 1..120 and bounds readback after the Windows start call. See [SDK service recovery](SDK_SERVICE_RECOVERY.md). |
| Advanced JSON | Edit the complete document, including logging and other top-level settings. Unknown `Studio5000` options, duplicate keys and invalid values are rejected. Transport overrides (`Kestrel`, `Urls`, `AllowedHosts`) and flattened configuration keys are rejected; use `ServiceUrl` for the loopback endpoint. |

**Broad local access** prepares a draft that enables all four access switches, enables SDK service recovery/preflight and clears every named target's method/program/routine policy. SDK operations can then use any explicitly supplied `path:` target. Named target aliases and CIP connection details are retained. The preset does not discover targets, start polling, launch SDK engines or apply controller changes. Save separately to stage the draft.

For example, `path:AB_ETH-1\192.0.2.10\Backplane\0` supplies a literal SDK route. A plain unknown alias is still rejected. Named targets retain their own policy unless it is edited/cleared; a direct `path:` target in broad mode is unrestricted. The global controller switch still applies. CIP reads require a configured target with explicit gateway/route details; enabling SDK paths does not invent CIP settings.

Configured roots remain useful preferred locations when unrestricted paths are allowed. Source bytes remain preserved and exports still require a new filename. Changing the state directory selects a different managed workspace; it does not move existing state.

## Saving and restart

The **Running service** tab shows effective configuration. The saved snapshot separately identifies pending changes and whether restart is required. **Save for next restart** validates the whole document, checks its exact revision, retains the original bytes under `.local/settings-history/` and atomically replaces the local file. Another client's or editor's changes reject a stale save with `settings_conflict`; reload/review before saving again.

Saving never alters live options or stops active jobs, polling or recordings. Finish work and restart/update the app when ready. **Reload saved** reloads the file into the editor; it does not apply it to the running service. A new service start applies the saved options. Default installation examples retain restricted file roots and disabled controller access; broad access is an explicit local choice and stays outside Git.

`MaximumMcpClients` defaults to 4 and accepts 1..16 Windows .NET frontends per installation. `McpShutdownTimeoutSeconds` defaults to 10 and accepts 1..30 seconds. Newly launched frontends read these saved fields directly; already connected frontends retain their settings. Lowering the limit never evicts a client, and held slots above the new limit still count. These limits do not cap HTTP requests or shared-service transactions. The optional Python frontend does not yet use these .NET admission leases. See [process lifetimes](PROCESS_LIFETIMES.md).

`MaximumOwnedProcesses` defaults to 32 and accepts 1..128 processes in the Windows service's private inherited-process job, including the service itself. This setting applies on service restart. A cap of 1 prevents ordinary worker launches. It does not count unrelated applications or external COM/service hosts, and it does not establish durable resource recovery. The GUI exposes it under **Runtime and limits**; containment runtime acceptance remains outstanding.

To stage the broad preset while a separate installed service keeps running, use a built service executable with `STUDIO5000_PLUGIN_ROOT` set to the intended app root and argument `--stage-broad-local-settings`. This administrative command uses the same Core store, writes a backup and returns a small receipt. It exits before starting the HTTP host, opening workspace state or creating any native session. It does not install binaries, restart a service or update the currently running service's policy.

## Checks that remain enforced

The **Always enforced** tab lists integrity and transport rules. HTTP stays on an authenticated loopback endpoint, browser-origin calls are rejected, originals are preserved, existing exports are not overwritten, preview/revision/hash and schema/SDK checks remain active, links/junctions are rejected, and audit/uncertain-job receipts are retained. These are separate from configurable target and file-access policy.

Codex and other MCP clients also have their own approval/write policies. This editor does not change another application's settings or chat approval mode. App settings do not authorize retrying an uncertain operation or substitute for its target/review arguments.

## REST and validation

- `GET /api/settings`: saved revision/document, effective document, changed settings and enforced checks.
- `POST /api/settings/broad-local-preview`: accepts the current full JSON document and returns the proposed broad profile without saving.
- `POST /api/settings`: `{ "expectedRevision": "...", "configuration": { ... } }`; returns the saved snapshot and private backup path.

All routes require the service bearer token. The APIs deliberately have no automatic restart or polling controls. The normal MCP operation catalog does not publish a settings-elevation tool.

`Verify-Isolated.ps1` checks staged saves, concurrent conflicts, invalid configuration, GUI broad-profile/save/restore and the offline staging command against disposable data. `Verify-SettingsStaging.ps1 -ServiceExe <isolated-service-exe>` checks the command separately, then starts its own temporary offline host to verify that empty root arrays survive configuration binding without a false restart requirement. It stops only that temporary host. No native controller is used.
