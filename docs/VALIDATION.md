# Validation and reproducibility

The default solution builds the core, REST service, transport client, MCP adapter, Windows desktop application and verification projects. The native bridge builds separately from the installed Rockwell SDK package. Package lock files pin restored dependencies.

## Automated checks

`scripts/Verify.ps1` runs:

1. **52 core tests:** Logix/ME/SE editing, SDK policy checks, exact target method/program restrictions, parent-program routine replacement and typed import targets, native empty-routine exports, accepted-edit diagnostic guidance, online-plan option mapping, no-connection preparation, hash validation, XML/sequence rejection, and separation of controller Test mode from native Test Edits. These tests require no native SDK.
2. **47 REST/MCP integration checks with this installed SDK and FactoryTalk schemas:** shared-service/authentication/edit/export checks, ME/SE XSDs/plans, SDK catalog/query, and an isolated online-planning service. That fixture uses an inert bridge with controller execution disabled; it tests MCP/REST plans, routine replacement mapping, rejection of the append-prone path, configuration binding, stale input, scope rejection and blocked execution without creating a native job. The official client discovers all 28 tools. Without the SDK the two local processor-query checks are omitted; without FactoryTalk schemas the 16 installed-schema checks are omitted. Graphics XML matches preserve native import as unobserved.
3. **Desktop render:** the actual main form, SDK editor and online-edit dialog load service data and render off screen; both ME and SE examples also render. This does not automate every button or validate native Studio Test/Untest/Assemble or graphics import.

Evidence files are `artifacts/test-results/core.trx`, `integration.json`, and `desktop.png`. `dotnet build CodexStudio5000.sln -c Release` completed with zero warnings and errors in this development environment.

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

No download, controller mode change, force, safety change or SD-card operation was requested. API-only FinalizeEdits on an initial live import remains unverified. This check does not establish general machine behavior, physical I/O, safety acceptance, all SDK controller operations or commissioning approval.

## Upstream source

`docs/upstream-manifest.json` joins source and copied SHA-256 values for all 24 upstream library files and hashes the supplied source ZIP and license. All 24 copied source files matched byte-for-byte during the audit. `.gitattributes` preserves those vendored bytes across Windows Git checkouts. No upstream source code changes were needed.
