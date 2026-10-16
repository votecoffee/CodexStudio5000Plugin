# Validation and reproducibility

The default solution builds the core, REST service, transport client, MCP adapter, Windows desktop application and verification projects. The native bridge builds separately from the installed Rockwell SDK package. Package lock files pin restored dependencies.

## Automated checks

`scripts/Verify.ps1` runs:

1. **22 core tests:** original Logix checks plus ME/SE detection, source preservation, schema routing (authored test schemas for portable unit tests), UTF-8 captions and connections, identity/structure protection, missing/invalid schemas, external schema rejection, import-plan targets/tamper checks, matching/mismatching/missing export evidence, reordered objects and compatibility with older manifests. Some tests cover multiple related assertions.
2. **31 REST/MCP integration checks in this installed environment:** the original shared-service/authentication/edit/export checks plus both ME/SE fixtures, actual installed Rockwell XSDs, graphics edits, import plans and simulated export verification. The official MCP client launches the actual PowerShell plugin wrapper and discovers all 20 tools. The graphics tests deliberately preserve native import as unobserved even when supplied file values match. When FactoryTalk schemas are absent, the 16 installed-schema integration checks are skipped explicitly; the portable core tests still run.
3. **Desktop render:** the actual WinForms form loads data through the shared HTTP client and renders off screen. Both ME and SE examples were visually inspected in the expanded GUI. This does not automate every button or validate Studio's native wizard.

Evidence files are `artifacts/test-results/core.trx`, `integration.json`, and `desktop.png`. `dotnet build CodexStudio5000.sln -c Release` completed with zero warnings and errors in this development environment.

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

No native `BuildAsync` verification, controller connection/download, online edits, forces, runtime behavior, physical I/O, safety acceptance, or commissioning is tested or implemented. Studio Custom Tools XML is generated from the provided upstream format but is not installed into the running Studio installation during validation.

## Upstream source

`docs/upstream-manifest.json` joins source and copied SHA-256 values for all 24 upstream library files and hashes the supplied source ZIP and license. All 24 copied source files matched byte-for-byte during the audit. `.gitattributes` preserves those vendored bytes across Windows Git checkouts. No upstream source code changes were needed.
