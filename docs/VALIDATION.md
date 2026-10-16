# Validation and reproducibility

The default solution builds the core, REST service, transport client, MCP adapter, Windows desktop application and verification projects. The native bridge builds separately from the installed Rockwell SDK package. Package lock files pin restored dependencies.

## Automated checks

The v0.8 browser/recording release adds **110 core tests** and **83 isolated REST/MCP checks** (with installed FactoryTalk schemas). New evidence covers paged tag/type/member browsing, a million-element schema array without value decoding, hidden/alias rejection with visible declared-bit selection, stopped immutable-source watch creation, journal restart/session recovery, old revision access, byte/age retention, cursor pagination, recording pause/resume, CSV quality/gap rows and formula neutralization. A corrupt complete journal entry is rejected, and an incomplete final append is recoverable. Numeric/BOOL projections break lines at missing/bad data and retain exact large-integer JSON values separately from floating-point graph coordinates.

The same MCP connection discovers and uses six new `watches` catalog operations without new fixed tool names. A 3 MiB reply crossed the actual worker/service/MCP transport after its bounded reply limit was increased to 12 MiB characters. Bundled catalogs advertise **53 operations**, and the fixed MCP count remains **56**. Eight desktop views, including the browser and trends, rendered off screen; the two new windows were visually inspected. NativeBridge compiles against the updated Core; no controller request or native Studio UI action is part of this release validation. Live polling/timing still requires the intended controller test.

The v0.8 evidence is under `artifacts/isolated-validation/1e78f828b1954ac69f6132e20e636112/artifacts/test-results/`. Earlier validation remains below as historical evidence.

For v0.7, `scripts/Verify-Isolated.ps1` passed against its own temporary service/port/state and published binaries. It never changes the normal connection/configuration or restarts the normal service; the native bridge is unconfigured and controller execution is disabled. Evidence is retained under `artifacts/isolated-validation/<run-id>/artifacts/test-results/`.

1. **98 core tests:** existing Logix/ME/SE editing, SDK scope/import/online-plan checks, watch scheduling/decoding/extraction, 15 View Designer tests and 13 extension tests. New extension coverage includes generation leases, jobs during reload, startup restoration, hash/contract/path rejection, immutable versions, schema/effect/stale-generation rejection, cancellation and omission of job argument payloads from persisted records. These tests require no native SDK.
2. **74 REST/MCP integration checks with installed FactoryTalk schemas and no native bridge:** the previous 60 checks plus 14 extension checks. The official client discovers 46 compatible tools and ten stable bridge tools. Two separately compiled fixture DLLs demonstrate v1-to-v2 reload, discovery/execution of a new operation over the SAME MCP connection, completion of the old job on v1 in a separate process, failed-reload preservation and explicit rollback. Bundled modules advertise 47 operations, including the extension-only HMI summary. Wrong-effect dispatch is rejected. Without FactoryTalk schemas, 16 schema checks are omitted. The regular `Verify.ps1` conditionally checks the native SDK/watch path; the isolated run intentionally has no native bridge or controller target.
3. **Desktop render:** the main form, online-edit dialog, both watch views, View Designer workspace and new Extensions form rendered off screen. The Extensions form generates fields from the supplied operation schema. The regular `Verify.ps1` retains conditional SDK/ME/SE rendering. This does not automate every button or validate native Studio editing/import/build/runtime behavior.

Evidence includes `core.trx`, `integration.json`, `extensions.json`, `desktop-main.png`, and `desktop-extensions.png` under the isolated run's test-results directory. `dotnet build CodexStudio5000.sln -c Release` completed with zero warnings and errors. The Extensions GUI includes editable IDs for newly installed modules. The native bridge also compiled against the updated Core/contract with zero warnings/errors; it was not executed by these isolated checks.

The normal shared service is left untouched during development. Publish/restart the v0.7 host/bridge only when current jobs/readers have finished, then refresh/reconnect MCP once to load the ten stable tools. Future installed extension features reload through those tools without reconnecting. No controller request was submitted by these development checks.

## Extension reload evidence (2026-10-16)

The fixture packages contain different compiled DLL hashes and different operation sets. A v1 background job holds its generation lease while the same MCP client activates v2 and calls `fixture_added_in_v2`. The new result reports version 2; the old job completes with version 1 and a different worker process ID. The old worker drains/disposes after the call finishes. A candidate with an incompatible contract is rejected while v2 stays available; explicitly reloading v1 removes the v2-only operation from the current catalog. Unit tests also check source tampering, malformed schemas, effect/argument validation and cancellation/restart as indeterminate.

This is actual worker/process/REST/MCP validation, with no OpenAI inference call or native controller/HMI interaction. It demonstrates the fixed bridge's reload contract with an official MCP client, not automatic refresh of changed Codex plugin manifests or skills. One-time installation/client refresh and future host/SDK/bridge contract upgrades remain separate. See [EXTENSIONS.md](EXTENSIONS.md).

### Local service deployment

After the user confirmed the other chat had finished using the API, deployment verified no active SDK jobs, watch readers or native bridge processes. The existing v0.5 binaries and local configuration were backed up under ignored `.local/deployments/`, then the service, MCP adapter, GUI, extension worker, native bridge and four initial packages were published. At that deployment the service reported **v0.7.0**, with `factorytalk`, `logix`, `viewdesigner` and `watches` active and 47 catalog operations; there were no package load errors.

An official MCP client connected to the published executable, discovered all 56 tools, reloaded View Designer to a new generation, described and invoked its read-only installation-status operation, and confirmed the same MCP connection and service PID remained in use. Local configuration bytes matched the preserved pre-deployment SHA-256. Controller operations remained disabled; no controller request was submitted. Final checks found no active SDK/extension jobs or watch readers. Deployment/transport receipts are retained beside the private backup and in the chat's `outputs/extensions/deployment/` directory. Existing Codex sessions can still require one client refresh to load the new fixed bridge tools; compatible extension updates thereafter use live discovery/reload.

## View Designer evidence (2026-10-16)

View Designer support follows the documented editable `.hmi` export format. Generated fixtures cover a ViewProject, screen with nested button/event/command parameters and a binding, popup, Add-On Graphic, shortcut, data log and SVG asset. Source and asset hashes remain unchanged. Two changed properties crossed REST/MCP preview/apply and import packaging; a copied export produced `property_values_match` with `nativeImportStatus=not_observed_by_service`. Unit tests also supply a mismatching export and require `property_values_mismatch`.

Installed V8/V9 executables were discovered without launching native UI. The user requested development without a native validation project today. **View Designer native export/import, catalog/property semantics, build/save, real re-export and panel runtime validation remain pending.** New screen/object creation and direct VPD/VPDR editing are not implemented. See [VIEW_DESIGNER.md](VIEW_DESIGNER.md) for exact support and handoff instructions.

Online-planning evidence is `online-edit-plan.json` and `desktop-online-edit.png`. The bundled `logix-online-edits` skill passes the skill frontmatter/placeholder validator. The initial implementation was validated locally; subsequent explicitly authorized version-35 work is recorded below. See [ONLINE_EDITS.md](ONLINE_EDITS.md).

## FactoryTalk evidence

`docs/FACTORYTALK.md` records ME/SE scope and boundaries. The supplied archive was checked against installed graphics schemas with:

```powershell
$env:STUDIO5000_PLUGIN_ROOT = (Get-Location).Path
dotnet run --project tests/Studio5000.Integration -c Release -- --factorytalk-corpus 'C:\...\FTViewME Screen Exports.zip'
```

The report `artifacts/test-results/factorytalk-corpus.json` records the archive hash, each input hash, schema hash and result. It contains **59 valid graphics XML files, one rejected ActiveX graphics file and two non-display batch XML files**. Corpus validation intentionally exits nonzero for that rejection. Neither the archive nor installed schemas were modified. Both authored ME/SE examples passed their actual installed XSDs and REST/MCP property edits.

The user stopped Computer Use with Escape at Studio's application-type selection. Native ME and SE wizard import/save/re-export were not tested, and no native HMI project was changed. File/schema checks and simulated re-export tests do not upgrade that pending native boundary. The bundled skill supplies the workflow for a later authorized native session.

## Native checks

`scripts/Verify-Native.ps1 -ProjectPath <allowed saved ACD>` exercises source snapshot → native L5X export → managed comment edit → native ACD SaveAs → native reopen/re-export → exact edited-comment lookup. It checks the original ACD hash before/after and saves `artifacts/test-results/native.json` when successful. If the source has no RLL rung, the test adds an unscheduled demonstration program to the managed working copy. It requires output access under the default `artifacts/exports` directory.

For an independent native fixture, `Studio5000.NativeBridge.exe --create-fixture <new.acd>` creates a version-35 `1756-L81E` project using the SDK. This fixture helper is not an MCP operation. Native support depends on the installed Logix version, SDK, FactoryTalk access, activation and project contents. A bridge build alone does not demonstrate native project acceptance.

**Independent fixture result: PASS.** A fresh SDK-created `1756-L81E`, revision 35.11 project was registered through the service. The service added an unscheduled RLL demonstration program and a unique rung comment, exported a new ACD, and the SDK reopened/re-exported that ACD. The marker appeared exactly once. The original fixture ACD's SHA-256 was unchanged. This demonstrates native Open/SaveAs/reopen for that fixture; it does not extend acceptance to the archived sample described below.

The archived `PLCSample.ACD` successfully opened and exported through SDK 2.1.974. However, the installed SDK rejected reopening its **untouched** L5X export with `XMLSrv_E_IMPORT_ABORTED_NO_CHANGES`. An edited export failed with the same error. This negative control is retained in managed staging; no generic recovery or native acceptance is claimed for that archived project. The service reports the failure and does not publish an ACD artifact. Its original ACD remains preserved. This is distinct from the synthetic REST/MCP tests and any successful independent native fixture.

The SDK NuGet package originally omitted `FtspAdapter.exe` from `dotnet publish` although it copied it into build output. The bridge explicitly includes that locally installed helper in publish output. No proprietary helper or client DLL is committed to this repository.

Version 0.3 implements the full installed SDK operation catalog. Native version-38 `BuildAsync` and additional offline feature verification are described in [SDK.md](SDK.md) and reproduced by `scripts/Verify-Sdk.ps1`; version 35 reported build unsupported. The original campaign was offline; subsequent bounded online reads/writes and import evidence appears below. Download, mode changes, signature generation/deletion and SD-card APIs remain unverified live. General runtime behavior, physical I/O, safety acceptance and commissioning remain unverified. There is no separate force API in this installed SDK surface. Custom Tools XML remains generated without modifying the Studio installation.

The expanded native run completed **48 distinct SDK operation names successfully** on generated offline fixtures. A 49th, get-safety-signature, reached the SDK and preserved the expected no-value error on a fixture without a signature. That offline campaign did not exercise the ten controller-only operation names. This counts operation names, not every argument combination or supported Logix version. All 12 typed tag read/write pairs and raw-byte access round-tripped, and all three partial-import APIs were followed by a successful build. Offline safety tests changed and cleared fixture passwords; the passwords were absent from retained results. The separate offline path test preserved its source hash and ran with controller operations disabled.

Cancellation of a dispatched generated-project job returned `indeterminate` and published no artifacts. The recovery unit test also marks an unfinished job indeterminate after service restart. These are process/result-boundary checks, not evidence that controller actions can be rolled back.

## Authorized version-35 program-local check (2026-10-16)

The user authorized a particular 1756-L82ES, firmware 35.11, and only `MainTask / CodexTest / main` with program-local test tags. Original ACDs and native exports were preserved and hashed in ignored local state. Target method/program restrictions were added to the shared service and verified through core and isolated REST/MCP checks.

- SDK UploadToNewProject completed. Initial service adoption failed because its XML validator rejected empty ladder routines without an RLLContent container; a generic fix and regression test now accept that native export form.
- The exact one-rung arithmetic payload first passed a version-35 offline import. It adds only local base tags `CodexSdk_Enable`, `CodexSdk_Input` and `CodexSdk_Result`. The readable content of the other sections and all 19 other programs matched the baseline. Three opaque protected AOI payloads differed in the saved export; their semantics and byte equality are not claimed.
- SDK GoOnline and ReadControllerMode reported Online / Run. PartialImportRungs with AcceptEdits succeeded with no import warnings/errors. The user reported the expected accepted rung and three local tags, with no other edits.
- Online BOOL/DINT writes and reads confirmed Enable true, Input 41 and Result -1 before native finalization. No standalone Test/Untest command was run.
- The user changed the requested workflow to direct finalization. An offline-only, routine-targeted replacement using FinalizeEdits failed with `XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM`, because the saved ACD already contained accepted edits. No live replacement was attempted. The user then reported finalizing CodexTest in Studio, clearing its edit state and observing Result 42.
- A subsequent API connection reported Online / Run, read Enable true, Input 41 and Result 42, then set Enable false and read it back false. This verifies this arithmetic result and scoped tag access; the cleared native edit state is user-reported.

No download, controller mode change, force, safety change or SD-card operation was requested. That phase did not verify API-only FinalizeEdits on an initial live import; the separate initial-import check below does. These checks do not establish general machine behavior, physical I/O, safety acceptance, all SDK controller operations or commissioning approval.

## Routine replacement regression (2026-10-16)

A native offline negative control imported a changed routine at the existing routine XPath. The SDK returned success but appended its rung, leaving two rungs. The corrected shared planner imports into the parent program with the exact routine target name, and scoped policies require matching Routine-target metadata. A fresh job through that planner/policy reported Offline throughout and exported exactly one routine and one changed rung. All 19 other programs and the three local tag definitions/data matched the baseline after normalized XML comparison; the source ACD hash was unchanged. Opaque protected AOI payload equality and runtime behavior are outside this offline check. An independent explicit-rung replacement also retained exactly one rung.

The 52 core tests and 47 REST/MCP checks include the corrected mapping and rejection of the old path. `Verify-Sdk.ps1` now inspects exported rung count and logic after partial imports, since SDK import/build success alone misses this failure.

## Initial API import and finalization (2026-10-16)

The same authorized version-35 controller/program/routine was used for a fresh API-only test after the user resumed work. A preflight API job reported Online / Run, Enable false, Input 41 and Result 42, and exported the existing single Input + 1 rung. The shared planner prepared explicit rung replacement at position 0, replaceCount 1, with `finalize_during_import`. Its import payload defined no tags/types and referenced only the three existing program-local test tags.

The live job disabled the test, set Input to 41 and Result to sentinel -1234567, then made exactly one PartialImportRungs call with `FinalizeEdits` from the outset. The SDK import returned zero warnings/errors. A native export contained exactly one Input + 2 rung, and the online reads after enabling returned Input 41 and Result 43. The user then reported **CodexTest shows No Edits** while instructed only to observe; no manual Accept, Test or Finalize step was requested or performed for this import. No separate test interval was provided.

An independent SDK session reopened the saved result, reported Online / Run, read Enable true, Input 41 and Result 43, then set Enable false and read it back false. Another process restarted the shared service during that session. The service correctly retained its job as indeterminate; the original native child subsequently completed with a successful receipt containing all eight calls and saved artifacts. That receipt was recovered separately without retrying the controller actions or rewriting the service job's status. This distinction is retained in the private evidence report.

The saved import export retained all 19 other programs after normalized readable XML comparison, one selected routine/rung and exactly the three local base tags. Source ACDs were preserved. A hash-verified ACD copy, the small routine export, request/results and the user observation are retained in the chat's `outputs/live-v35/initial-finalize/` directory; private job paths remain under ignored `.local`. This establishes the bounded API-only import/finalize and arithmetic check for this controller/version. Native Test/Untest, other PLC features/versions and FactoryTalk runtime/import acceptance are not established by this check.

After the user confirmed the separate work had finished, cleanup verified no active SDK jobs or native bridge processes, disabled controller operations in local configuration, restarted the shared service and verified its runtime controller gate was disabled. No controller request was submitted during cleanup. The service remains available for offline/API/GUI work. Later controller work completed separately, so the retained test ACD and tag readings are historical validation evidence rather than a claim about the latest controller state.

## Watch lists and UDT structures (2026-10-16)

The new watch/pull/extraction feature was validated without connecting to a controller; controller execution remained disabled. An SDK-created version-38 fixture supplies nested user-defined types, an INT member array, an array of UDTs, a BOOL and a STRING. Native raw UDT reads and an isolated offline BYTE_ARRAY write/export demonstrated the SDK decoding path. Persistent-reader integration subsequently polled that fixture repeatedly, checked nested Count 8, array value 20, BOOL true, STRING `Watch ready` and selected UDT-array member 55. History stayed bounded and cycles did not overlap; stopping disposed the native reader, cached values became stale and the original source hash was preserved.

An initial two-minute test timeout expired while loading the expanded fixture. Startup now imports L5X once into a private ACD before opening reader/decoder copies, and the native test permits five minutes for cold loading. Once started, the fixture polled at the configured interval. This is fixture-specific timing, not a real-time guarantee. Both desktop watch settings and the nested-values/tree view rendered off screen. The 70 core tests also cover nested STRING members/ASCII bytes, multi-dimensional signed-radix values, root-array selection, failure isolation, stopped-only revisions, source drift, disabled live pulls and upload-origin labeling.

Evidence is `artifacts/test-results/core.trx`, `integration.json`, `watches.json`, `desktop-watches.png` and `desktop-watches-values.png`. The GUI render samples are display fixtures. Live interval polling, controller schema changes and fresh upload/UDT extraction remain pending for an explicitly authorized controller/version; prior live online-edit checks do not establish these new boundaries. See [WATCHES.md](WATCHES.md).

## Upstream source

`docs/upstream-manifest.json` joins source and copied SHA-256 values for all 24 upstream library files and hashes the supplied source ZIP and license. All 24 copied source files matched byte-for-byte during the audit. `.gitattributes` preserves those vendored bytes across Windows Git checkouts. No upstream source code changes were needed.
