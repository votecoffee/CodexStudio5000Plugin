# Validation and reproducibility

The default solution builds the core, REST service, transport client, MCP adapter, Windows desktop application and verification projects. The native bridge builds separately from the installed Rockwell SDK package. Package lock files pin restored dependencies.

## Automated checks

`scripts/Verify.ps1` runs:

1. **10 core tests:** preserved source bytes, UTF-8 text, preview/apply/restart behavior, idempotent retry, competing-client conflicts, malformed XML/DTD rejection, duplicate declarations, repeated dependency references, path/hash protection, XML component operations, explode/recompose retention of RLL/ST logic and ExportDate, native-unavailable errors, and single-service ownership. Some tests cover multiple related assertions.
2. **15 REST/MCP integration checks:** real bearer authentication and browser-origin rejection, shared state across both clients, preview isolation, apply, export/download hash equality, new-file saving, preservation of existing outputs and original sources, stale revision rejection, audit events, and discovery of all 11 tools by the official MCP client.
3. **Desktop render:** the actual WinForms form loads a project through the shared HTTP client, lists a routine and displays its XML, then renders off screen. The saved PNG is manually inspected for layout. This does not automate every interactive button or validate Studio's native UI integration.

Evidence files are `artifacts/test-results/core.trx`, `integration.json`, and `desktop.png`. `dotnet build CodexStudio5000.sln -c Release` completed with zero warnings and errors in this development environment.

## Native checks

`scripts/Verify-Native.ps1 -ProjectPath <allowed saved ACD>` exercises source snapshot → native L5X export → managed comment edit → native ACD SaveAs → native reopen/re-export → exact edited-comment lookup. It checks the original ACD hash before/after and saves `artifacts/test-results/native.json` when successful. If the source has no RLL rung, the test adds an unscheduled demonstration program to the managed working copy. It requires output access under the default `artifacts/exports` directory.

For an independent native fixture, `Studio5000.NativeBridge.exe --create-fixture <new.acd>` creates a version-35 `1756-L81E` project using the SDK. This fixture helper is not an MCP operation. Native support depends on the installed Logix version, SDK, FactoryTalk access, activation and project contents. A bridge build alone does not demonstrate native project acceptance.

**Independent fixture result: PASS.** A fresh SDK-created `1756-L81E`, revision 35.11 project was registered through the service. The service added an unscheduled RLL demonstration program and a unique rung comment, exported a new ACD, and the SDK reopened/re-exported that ACD. The marker appeared exactly once. The original fixture ACD's SHA-256 was unchanged. This demonstrates native Open/SaveAs/reopen for that fixture; it does not extend acceptance to the archived sample described below.

The archived `PLCSample.ACD` successfully opened and exported through SDK 2.1.974. However, the installed SDK rejected reopening its **untouched** L5X export with `XMLSrv_E_IMPORT_ABORTED_NO_CHANGES`. An edited export failed with the same error. This negative control is retained in managed staging; no generic recovery or native acceptance is claimed for that archived project. The service reports the failure and does not publish an ACD artifact. Its original ACD remains preserved. This is distinct from the synthetic REST/MCP tests and any successful independent native fixture.

The SDK NuGet package originally omitted `FtspAdapter.exe` from `dotnet publish` although it copied it into build output. The bridge explicitly includes that locally installed helper in publish output. No proprietary helper or client DLL is committed to this repository.

No native `BuildAsync` verification, controller connection/download, online edits, forces, runtime behavior, physical I/O, safety acceptance, or commissioning is tested or implemented. Studio Custom Tools XML is generated from the provided upstream format but is not installed into the running Studio installation during validation.

## Upstream source

`docs/upstream-manifest.json` joins source and copied SHA-256 values for all 24 upstream library files and hashes the supplied source ZIP and license. All 24 copied source files matched byte-for-byte during the audit. `.gitattributes` preserves those vendored bytes across Windows Git checkouts. No upstream source code changes were needed.
