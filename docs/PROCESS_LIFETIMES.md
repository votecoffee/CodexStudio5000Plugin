# Process ownership and lifetimes

## Application owners

Each app keeps its own Codex plugin and native skills. The shared framework and independent Windows manager provide common infrastructure. No universal plugin or aggregate MCP connection is selected. The actual surviving application or transport process owns its generation; registration helpers never stand in for it.

The shared .NET service owns domain operations and its extension, CIP and SDK workers. Direct startup publishes a full creation-bound owner receipt and persists independently of its launching shell. Managed startup additionally observes the Python supervisor. Private workers use atomic suspended creation with a Windows job and exact redirected handles; identity is durable before execution. There is no late assignment, process-name adoption or PID-file authority.

The bounded self-owned service job remains rooted until process exit. Ordinary inherited children cannot break away. Nested jobs allow precise cleanup. Abrupt owner death closes private kernel jobs but does not establish save, rollback or known external effects. SDK, COM, WMI and Windows-service software outside a private job needs explicit identities and app-owned recovery. See the [resource ledger and native-session contract](RESOURCE_LEDGER.md).

## Managed native admission and shutdown

The managed profile is `owned-service-v2`; the earlier offline-only profile is not adopted. Native SDK/CIP paths and controller settings remain available under domain authorization. Python retains a service-tree shutdown barrier before publishing supervisor metadata. Core verifies the exact service process, parent generation, host/image/creation identity, live parent, private Windows job and barrier before initializing business services. Native admission cannot race that handshake.

Both direct and managed `Stop-Service.ps1` paths send signed private owner requests. They require exact creation-bound identities; missing legacy evidence refuses PID termination. Ctrl+C uses the same owner mailbox instead of bypassing native refusal. The service fences mutations, polling, native jobs and extension calls, then checks native/shared/dirty/uncertain barriers before teardown. A verified zero-effect refusal keeps the owner running. Only explicit `-ReviewBlockedStop` after resolving that refusal permits another review. A lost dispatch result remains uncertain and never automatically retries.

The service writes its exact exit receipt only after connections and owned resources close. The supervisor requires matching evidence plus observed service exit before resolving its barrier and retiring its job. An unverified exit or unavailable identity retains the supervisor and barrier. Reporting, IPC and ledger failures do not unwind a native owner. No restart, adoption, write replay or reconciliation is automatic. Prior job and recovery records remain authoritative.

The common manager stays conservative about overlapping jobs. Only the app's current inner service owner may retire its worker despite the exact outer service-root snapshot. Attachments, siblings, foreign apps, receipt-bearing and departed generations still block it. This service-only delegation grants the manager no new cleanup rights.

## Native sessions and user-owned GUI

SDK operation, conversion and watch workers have private process records and separate report-only native-session claims. Exit alone cannot clear a native claim: an exact worker/session receipt must attest saved or explicitly discardable private documents, known effects and closed connections. Successful calls have individual checkpoints. Failed calls retain pending-effect evidence and may preserve a separate private recovery ACD. Shared references invoke close only at the last local lease. Dirty refusals and uncertain results remain retained; last-owner status never authorizes terminating shared or user-owned software.

FactoryTalk GUI launch uses the existing user desktop shell. The service pins that provider's explicit identity, records its COM attachment and flushes an intent before dispatch. Studio and subsequent user-opened documents remain user-owned. The service releases only its shell connection; it never adds the GUI to its private job or saves, closes or terminates it. The shell does not return the target instance identity; receipts explicitly report that unknown. Observe Studio to confirm startup. Uncertain handoffs block replay. View Designer file preparation similarly does not adopt a native GUI.

## MCP frontend lifetime

The plugin bootstrap and optional framework backend launch `Studio5000.StdioHost`, which survives as owner of the actual .NET MCP adapter. It preserves raw bytes and stdin EOF; the existing MCP SDK remains the parser. EOF gets bounded cooperative shutdown before this frontend's private job closes. Launcher/parent handles are pinned. This owner never starts or stops the shared HTTP service.

The .NET adapter and optional Python remote frontend share OS-owned, share-none admission slots. All sixteen possible slots are counted when a limit is lowered; no client is evicted. Metadata never authorizes stopping a PID. Parent/wrapper identities are pinned up to four ancestors. Python's public SDK stdio lifecycle cancels on parent loss and releases observation/admission handles on EOF. Disconnect does not cancel shared jobs, watches or another frontend and implies no rollback.

The optional framework publication pins the complete immutable adapter/stdio-owner closure and retains the transient/legacy SDK transport profile. The background-service launcher is never substituted for stdio and does not inherit protocol streams. Source changes do not update an installed runtime automatically.

## Limits and maintenance

GUI **Settings > Runtime and limits** exposes `MaximumOwnedProcesses` (default 32, 1..128), `MaximumOwnedResources` (default 32, 1..256), `MaximumMcpClients` (default 4, 1..16) and `McpShutdownTimeoutSeconds` (default 10, 1..30). Service/resource limits apply at startup; frontend limits apply to new frontends. The ledger separately bounds history at 4,096 records, each resource at 32 KiB and membership at 256 identities. Capacity refusal preserves evidence.

A `.local/maintenance-hold.json` marker blocks launch, install, runtime selection, new worker/GUI admission and ordinary source server entry points. No update or cleanup clears it automatically. Existing settings, native/extension journals, private packages and uncertain receipts remain preserved. Previously installed executables need separate version verification.

Manager restart remains disabled: there is no atomic restart contract covering every chat, user-owned unsaved document, active recording, prepared review and unresolved external effect.

## Validation boundaries

Policy tests use generated files, synthetic providers/process drivers and SDK-lifecycle stubs. Cross-runtime fixtures check canonical resource bytes, revisions and locks. Opt-in `test_dotnet_launch.py` and `test_ownership_processes.py` launch only disposable Python/helper processes in private registries, checking streams, EOF, bounded timeout, descendants, owner death, pre-resume failure and refusal. `Verify-OwnedServiceStop.ps1` uses process-object stubs in both PowerShell versions and never stops an actual PID.

These checks do not establish actual engineering host, native, controller, installed plugin or Codex acceptance. Controlled runtime checks need explicit authorization and disposable targets: SDK/save/dispose behavior, desktop COM handoff, actual SDK stdio EOF/parent crash, concurrent admission/reconnect, startup failure and owner-mediated shutdown/orphan review. Keep package, protocol, application, native and controller evidence separate.
