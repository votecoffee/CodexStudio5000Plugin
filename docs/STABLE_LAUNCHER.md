# App-owned stable MCP launcher

This is a prepared, guarded deployment contract. Preparing a candidate or plan does not activate it, edit Codex configuration, replace a running application or establish native acceptance. Existing installations keep their selected route until an explicit handoff.

The proposed Codex command and working directory are stable app-owned paths:

```text
%LOCALAPPDATA%\RockwellMCPServer\Start-Mcp.ps1
%LOCALAPPDATA%\RockwellMCPServer
```

The launcher reads `launcher/current.json`, verifies its selection checksum and complete backend inventory, then starts the immutable `Studio5000.StdioHost.exe`. That existing owner creates the MCP adapter suspended in its private Windows job, publishes creation identity before execution, and retains normal EOF/parent-lifetime cleanup. No alternative transport, broker or framework is introduced.

The snapshot contains the complete published MCP/stdio/Core dependency closure. It does not depend on a Codex cache or checkout launcher. It still uses the explicit selected application root for existing settings, credentials, state and the independently running shared service. Those files are referenced, never copied into a plugin/backend package or rewritten by publication. The launcher starts no shared, native or Windows SDK service.

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

The [exited-frontend recovery policy](RESOURCE_LEDGER.md) retains exact executable-path/full-generation guards. It recovers identified dead transports within one immutable backend path. A different legacy/backend path requires separately reviewed reconciliation; publication does not adopt it or clear unbound/native uncertainty. Old snapshots are not automatically garbage-collected.

## Evidence boundary

`tests/python/test_stable_launcher.py` uses Windows PowerShell 5.1 and PowerShell 7 with a compiled harmless stdio stand-in: cache removal, Unicode paths/data, EOF, source/selection revision guards, tamper refusal, history/idempotence, maintenance holds and preserving a running backend across pointer rotation. Tests use disposable roots and never load Rockwell SDK/native engineering code, contact a controller, change the real app-owned selection or write global Codex configuration.

Inventory/checksums detect drift in private trusted-user files; they are not signatures or protection against the same authorized Windows user replacing the trust root. State is restricted to the current user and SYSTEM. Source/native/controller/GUI and installed-chat acceptance keep separate evidence requirements.
