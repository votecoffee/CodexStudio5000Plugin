# Validation and reproducibility

Use generated fixtures and preserved source copies. Keep run reports, native exports, screenshots and controller observations local: `artifacts/` holds generated results, and `.local/private-docs/` holds private notes and deployment receipts. Neither belongs in public commits or issue attachments without review and redaction. Test results describe the environment and inputs used for that run.

## Automated checks without a controller

From the repository root on Windows with the .NET 8 SDK:

```powershell
dotnet test tests/Studio5000.Tests/Studio5000.Tests.csproj -c Release
.\scripts\Verify-Isolated.ps1
```

The isolated script publishes a separate service, MCP adapter, desktop application and extension worker under `artifacts/isolated-validation/<run-id>/`. It selects its own loopback port and state, leaves the normal service configuration alone, unconfigures the native bridge and disables controller operations. It runs core tests, REST/MCP integration checks through an official MCP client, extension reload checks and thirteen off-screen desktop renders. It stops its temporary service when finished.

Results are under that run's `artifacts/test-results/`, including `core.trx`, `integration.json`, `extensions.json` and desktop PNGs. FactoryTalk schema-dependent checks require the matching installed ME/SE schemas; inspect skipped checks rather than treating them as passes. Rendering a desktop view checks layout, not every button or native Studio workflow.

The checks cover:

- Logix XML editing, source preservation, stale hashes and component/scope validation, plus structural comparison, lexical tag where-used and passive ladder previews.
- FactoryTalk existing-property edits, installed-schema validation and simulated re-export mismatches; explicit shortcut/scoped-parameter resolution, saved UDT/array/bit checks, missing context, cycles, hash rejection and response budgets.
- View Designer nested properties, bindings, encoding preservation, unchanged assets and import packages using generated fixtures.
- Tag/UDT browsing, paged arrays, watch scheduling/decoding, journal recovery/retention, CSV quality rows and graph gaps.
- Extension package hashes/contracts, generation pinning, reload over the same MCP connection, old-job completion, rejected reloads and rollback.

`scripts/Verify.ps1` instead uses the configured running service and conditionally exercises its native bridge. Choose the isolated script for ordinary development without native execution.

## Optional offline Logix SDK checks

For a focused **v38** check that publishes a separate native bridge and uses an isolated shared-service instance with no controller targets, run `scripts/Verify-LogixVersion.ps1 -Revision 38`. It checks generated native creation/build/save, edited L5X-to-ACD conversion, local DINT/LREAL/ULINT values and reopening. It does not use or restart the normal service. See [version support and detailed boundaries](LOGIX_VERSIONS.md).

The broader checks below require a licensed installed SDK, compatible Logix Designer versions and a configured running service. Compile the native bridge with `scripts/Build.ps1 -WithSdk`; compilation establishes bindings, not native acceptance.

```powershell
.\scripts\Verify-Sdk.ps1
.\scripts\Verify-Native.ps1 -ProjectPath 'C:\Projects\ValidationFixture.acd'
```

`Verify-Sdk.ps1` defaults to generated version-38 standard and GuardLogix projects. It exercises offline builds, typed/raw tag round trips, partial imports/exports, conversions and offline safety operations. Its communications-path fixture uses a TEST-NET address with controller execution disabled and never connects. Reports are saved under `artifacts/test-results/sdk-<run-id>/`. Operation availability varies by installed version; inspect per-call diagnostics and expected negative cases.

`Verify-Native.ps1` hashes the supplied ACD, snapshots it through the service, edits a rung comment in the managed copy, saves a new ACD and reopens/re-exports it to find the marker. For an empty project it adds an unscheduled demonstration program to the copy. The source must be under an allowed root; outputs need an allowed output directory. A successful run writes `artifacts/test-results/native.json` and checks that the source hash remains unchanged.

Check the exported routine/rung count and expected logic after partial imports. A successful SDK import or build alone cannot detect unintended appending. For failures, retain an untouched export/reimport control before attributing the problem to an edit. See [SDK.md](SDK.md) and [ONLINE_EDITS.md](ONLINE_EDITS.md).

## Native HMI validation

FactoryTalk fixtures in `examples/DemoME.xml` and `examples/DemoSE.xml` exercise installed schema validation. Generated View Designer fixtures are under `examples/ViewDesignerDemo`. These samples and simulated re-exports do not establish native import, build or runtime acceptance.

Validate against a disposable native project copy with the intended application/edition, panel type and language. Preserve a native backup, import the prepared package, inspect messages, build/save where applicable and re-export the same content to a fresh location. Compare that actual export through the service and record the native observations separately. Matching XML/HMI properties alone do not authenticate their native origin or runtime behavior.

Follow [FACTORYTALK.md](FACTORYTALK.md) and [VIEW_DESIGNER.md](VIEW_DESIGNER.md) for the supported workflow and boundaries. Native acceptance must be established for the intended project and installed version.

## Live controller validation

Development and offline checks do not authorize a live connection. A live run needs an explicitly authorized controller/Echo target, matching saved ACD, bounded program/routine and tag scope, an edit-state check and functional criteria. Controller operations are disabled by default.

Record source/payload hashes, attempted SDK calls, diagnostics, tag observations and native edit states in private local records. Keep API measurements distinct from native Studio observations. `FinalizeEdits` during an import provides no separate test interval; controller Test mode is not Test Edits. See [ONLINE_EDITS.md](ONLINE_EDITS.md).

Neither offline checks nor a bounded live test establish general physical I/O, safety or commissioning acceptance. Interrupted jobs can be indeterminate; cancellation is not rollback and must not trigger an automatic retry.
