# App-owned stable MCP launcher

This is a prepared, guarded deployment contract. Preparing a candidate or plan does not activate it, edit Codex configuration, replace a running application or establish native acceptance. Existing installations keep their selected route until an explicit handoff.

The proposed Codex command and working directory are stable app-owned paths:

```text
%LOCALAPPDATA%\RockwellMCPServer\Start-Mcp.ps1
%LOCALAPPDATA%\RockwellMCPServer
```

The launcher reads `launcher/current.json`, verifies its selection checksum and complete backend inventory, then starts the immutable `Studio5000.StdioHost.exe`. That existing owner creates the MCP adapter suspended in its private Windows job, publishes creation identity before execution, and retains normal EOF/parent-lifetime cleanup. No alternative transport, broker or framework is introduced.

The snapshot contains the complete published MCP/stdio/Core dependency closure and a reviewed offline-service pin. It does not depend on a Codex cache or checkout launcher. It uses the explicit selected application root for existing settings, credentials, state and the ordinary shared service. Those files are referenced, never copied into a plugin/backend package or rewritten by publication. The static launcher starts the frontend; shared Core safely ensures the pinned offline service from the real long-lived stdio supervisor outside the MCP child's private kill job.

## Offline shared-service startup

Generate the required hash-bound payload before creating the candidate inventory:

```powershell
.\scripts\New-OfflineServicePin.ps1 -AppRoot C:\Apps\RockwellMCPServer -Destination C:\Prepared\Candidate\Mcp\offline-service.json -ExpectedServiceVersion <reviewed-serving-version> -PredecessorStateRoot C:\Users\Example\AppData\Local\RockwellMCPServer
```

The pin binds the existing ordinary service and extension-worker file inventories, starter script, expected serving version, and up to eight reviewed predecessor frontend closures. Product-version disagreement, links, duplicate/traversal members and file drift refuse. Full application builds generate a pin for their just-published service. A frontend-only update can pin the existing accepted service without replacing its binaries or changing engineering settings.

MCP metadata initialization and tool listing respond while readiness runs in the background. Before any engineering HTTP request is dispatched, the client waits for a genuine matching service owner, loopback health and authenticated effective-policy readback. This does not replay HTTP writes, native jobs or customer operations. A verified departed service generation can be ensured again on demand during the same chat.

Automatic startup requires explicit controller operations, SDK recovery and SDK autostart flags to be false, native SDK/CIP paths to be null, disabled native HMI launchers and the pinned extension-worker path. A selected optional management host, maintenance hold, active update, unmatched listener, unknown owner or changed runtime refuses automatic startup. Native-profile startup has no fallback through this offline contract.

An exclusive startup lock and the existing settings lock coordinate competing callers. The pinned ordinary starter runs once with file-backed standard handles; a departed wrapper cannot hold MCP protocol pipes open. The actual service publishes its own surviving owner before serving work. Frontend EOF never owns or terminates that service. An unresolved startup dispatch retains immutable intent/dispatch/readiness receipts and blocks another dispatch; observing a genuine matching healthy service can resolve availability without replay.

Only positively exited, fully identified pinned service-worker or reviewed predecessor-frontend claims can be reconciled. Live/unknown generations, unbound reservations, shared/user software, native barriers and operation journals stay retained. Lock acquisition waits at most five seconds for Windows sharing/byte-lock contention; the guarded metadata operation is dispatched once after acquisition. The configured resource limit is unchanged. If retained uncertain claims and legitimate owners consume it, admission still refuses.

The OS may redirect logical Local AppData paths for Store applications. Preserve full captured process-image identities; use the resolved physical stable entrypoint/cwd when preparing the client handoff. New stdio owners derive the adapter from their actual image directory. Metadata-only path inspection and an actual protocol/cleanup proof remain separate evidence.

## Candidate and plan

Publish the MCP adapter and stdio owner into one separate `Mcp/` directory from the same clean source revision. Create a candidate manifest with `version`, `sourceCommit`, `sourceTreeClean:true`, `kind:"MCP frontend closure only"`, `state:"staged-not-selected"`, `coreSha256`, and `payloads:[{path:"Mcp/<relative-file>",sha256,size}]`. Bind its exact bytes with a separately reviewed SHA-256. Compiler/test/provenance evidence belongs with that review; the publisher verifies reviewed bytes and cannot authenticate build authorship.

```powershell
.\scripts\Publish-StableMcp.ps1 `
  -SourceDirectory C:\Prepared\Candidate\Mcp `
  -CandidateManifest C:\Prepared\Candidate\candidate.json `
  -ExpectedManifestSha256 <reviewed-manifest-sha256> `
  -AppRoot C:\Apps\RockwellMCPServer `
  -ExpectedPointerSha256 absent `
  -PlanOnly
```

`-PlanOnly` checks the input closure and existing selection, reports exact proposed command/arguments/cwd and writes nothing. The default app-owned root is outside Codex caches. `-StateRoot` supports a separate staging root or explicit local deployment root. Source/state overlap, cache-owned state, links, junctions, traversal, duplicate/extra files and stale hashes are refused.

For an existing selection, supply the exact current pointer file SHA instead of `absent`. The guarded writer rechecks under an exclusive publication lock. It creates a content-addressed snapshot, copies and verifies every file, preserves earlier selections/launcher bytes, then atomically publishes the pointer. It does not execute the backend or call Codex. Failed publication retains staging/temporary evidence; inspect it before another request.

The backend directory uses the first 32 hex digits of its full SHA-256 identity to keep Windows executable paths short. The full identity, version, source manifest hash, source commit and ordered per-file hashes/sizes are revalidated; a prefix collision refuses publication. An executable path exceeding the supported Windows startup budget is rejected before selection.

## Reviewed handoff

1. Retain the exact existing route, selected offline/native policy, source/pointer hashes, live owner/child generations and unresolved journals. Review the candidate and plan first.
2. Publish only the reviewed backend to the intended app-owned root with the expected old pointer hash. Selection in a staging root does not change the real installation.
3. Read back with `Inspect-StableMcp.ps1 -StateRoot <root>`. This checks bytes/settings identity without starting a backend or editing configuration.
4. At the authorized client/reboot checkpoint, the designated configuration writer applies only reviewed command/arguments/cwd to the existing Rockwell route. Preserve unrelated policies, the standalone plugin/native skills and deliberate bundled-server enablement state. Retain exact configuration bytes/line endings and backup; keep one canonical route.
5. Verify one actual saved-route initialize/discovery/guidance/EOF session and requesting-chat visibility separately. Harmless launcher tests do not establish loaded chat tools.

The publisher deliberately has no Codex configuration writer. Existing installer/cache bindings are migration inputs; preparation does not silently promote them or install a full service/GUI/native runtime. Promotion around a reboot remains an explicit guarded handoff.

A pointer change affects subsequent frontend starts. A running backend keeps its immutable files and ordinary lifecycle; no process is stopped or restarted. Current connections do not acquire new loaded MCP tool definitions merely because a pointer changed. Shared service/extension updating remains independent.

The [exited-frontend recovery policy](RESOURCE_LEDGER.md) retains exact executable-path/full-generation guards. Different predecessor paths require explicit reviewed closure pins before reconciliation. Unbound records remain protected unless the separately reviewed legacy stateless contract below applies. Old snapshots are not automatically garbage-collected.

## Reviewed legacy stateless reservations

`New-OfflineServicePin.ps1 -ReviewedLegacyUnboundContract -PredecessorStateRoot <reviewed-root>` explicitly opts the pinned predecessor into `windows-private-job-bind-before-resume-v1`. It is off by default and accepts only source revision `31a57922bde082f82567ebcc035b29ab0a35d9da`. Review the original publication inventory and the launch implementation before enabling it. The pin binds the exact predecessor directory, complete file inventory, original manifest hash and source revision. These trusted local inventories detect drift; a source-commit string alone is not independent build provenance or a signature.

That implementation creates a suspended adapter in a private, non-inherited kill-on-close Windows job and durably binds its identity before resuming it. A compatible unbound claim can be retired only after the full original owner generation is positively absent or replaced. The record must have the exact transport kind/label and launching/private/terminate/app-owned shape, no process or members, no receipt or recovery barrier, and no conflicting retained identity claim. Unknown or live owners, other binaries, native resources and operation journals remain protected. Retirement performs no process action, invents no child PID, and atomically retains the original owner plus the previous record revision, actual current actor and proof hash.

Run the candidate's `Studio5000.StdioHost.exe --offline-bootstrap-preview` with the intended app root and registry environment to inspect eligibility and capacity without admitting a frontend, starting a service or changing ledger files. Review the exact current records and pin; a read-only preview is not an actual transport acceptance test. On admission, each record revision and owner state are checked again under the shared lock. Resource/client limits stay unchanged.

The opt-in original-image process fixture copies a verified predecessor closure into disposable state. It gates the unbound bind window, crashes only the retained original creation handle, and checks the unpublished adapter's retained handle signals when the private job closes. This proves an owner-crash case; it does not mislabel a fixture-held lock timeout as a bind failure or establish acceptance of historical native work.

## Evidence boundary

`tests/python/test_stable_launcher.py` uses Windows PowerShell 5.1 and PowerShell 7 with a compiled harmless stdio stand-in: cache removal, Unicode paths/data, EOF, source/selection revision guards, tamper refusal, history/idempotence, maintenance holds and preserving a running backend across pointer rotation. Tests use disposable roots and never load Rockwell SDK/native engineering code, contact a controller, change the real app-owned selection or write global Codex configuration.

Inventory/checksums detect drift in private trusted-user files; they are not signatures or protection against the same authorized Windows user replacing the trust root. State is restricted to the current user and SYSTEM. Source/native/controller/GUI and installed-chat acceptance keep separate evidence requirements.
