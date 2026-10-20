# Validation and reproducibility

Use generated fixtures and preserved source copies. Keep run reports, native exports, screenshots and controller observations local: `artifacts/` holds generated results, and `.local/private-docs/` holds private notes and deployment receipts. Neither belongs in public commits or issue attachments without review and redaction. Test results describe the environment and inputs used for that run.

## Automated checks without a controller

From the repository root on Windows with the .NET 8 SDK:

```powershell
dotnet test tests/Studio5000.Tests/Studio5000.Tests.csproj -c Release
.\scripts\Verify-Isolated.ps1
```

The isolated script publishes a separate service, MCP adapter, desktop application and extension worker under `artifacts/isolated-validation/<run-id>/`. It selects its own loopback port and state, leaves the normal service configuration alone, unconfigures the native bridge and disables controller operations. It runs core tests, REST/MCP integration checks through an official MCP client, extension reload checks and 33 off-screen desktop checks/renders. It stops its temporary service when finished.

Results are under that run's `artifacts/test-results/`, including `core.trx`, `integration.json`, `extensions.json` and desktop PNGs. FactoryTalk schema-dependent checks require the matching installed ME/SE schemas; inspect skipped checks rather than treating them as passes. Rendering a desktop view checks layout, not every button or native Studio workflow.

The checks cover:

- Legacy review-copy preparation: completed offline native export provenance, exact hash/field guards, BOM and unselected byte preservation, ambiguous/unsupported syntax rejection and no native jobs. The v35-to-v32 readback profile separates readable code/ordinary values from motion/MESSAGE configuration and protected AOI envelopes/full opaque payloads. Generated fixtures establish offline behavior; native acceptance remains case-specific.

- Logix XML editing, source preservation, stale hashes and component/scope validation, plus structural comparison, lexical tag where-used and passive ladder previews.
- Whole-tag value merging: exact UDT/array layout checks, malformed/incomplete values, explicit mappings, stale review/hash rejection, original/metadata/logic preservation and separate offline L5X export. Online capture is rejected with no job/connection in isolated validation.
- Independent routine/ladder comparison: program/AOI identity, changed/unchanged/one-sided routines, aligned insertion gaps, changed-row filtering, both diagrams and retained parse diagnostics. Generated GUI renders cover the merge window and integrated ladder browser.
- FactoryTalk existing-property edits, installed-schema validation and simulated re-export mismatches; explicit shortcut/scoped-parameter resolution, saved UDT/array/bit checks, missing context, cycles, hash rejection and response budgets.
- View Designer nested properties, bindings, encoding preservation, unchanged assets and import packages using generated fixtures.
- Tag/UDT browsing, paged arrays, watch scheduling/decoding, journal recovery/retention, CSV quality rows and graph gaps.
- Alias/AOI parameter dependency tracing across routines, conservative ST assignments, captured rung true/false/unknown explanations and immutable historical revision binding.
- Multilingual documentation: explicit languages versus plain text, tag/member/rung and component scopes, consistent legacy promotion, unknown/duplicate shape rejection, Unicode/XML-like text, atomic failures and preserved original/code/value/language metadata. REST/MCP share all extraction/preview/apply/export operations.
- Triggered pre/post windows using generated watch readers, communication-fault/stop interruption, observed timeouts, restart disarming, saved trend edits, bounded result pages and verified exports. No real reader/controller is opened.
- Common-baseline logic/value merges, conflicting independent value edits, explicit resolutions and separate-copy/source preservation. Whole-project preservation tests cover thousands of tags, over 100000 XML elements, all saved representations, exact MESSAGE fields, raw CDATA/line-ending retention, schema/safety conflicts and stale policies. Named validation tests cover expected versus unintended drift, new/inherited/resolved references, opt-in native formatting rules and immutable full-policy receipts. Private real-project assessments remain outside the public fixtures. Version compatibility checks cover native plans, unavailable/downgrade rejection and generated readback/configuration diagnostics; end-to-end native conversion/readback validation is separate.
- Read-only watch preflight for drafts, saved definitions and managed browser snapshots: per-row diagnostics, stale hashes, configuration gates, deferred ACD/templates and combined CIP budgets. Generated GUI rendering covers the preflight viewer.
- Portable watch templates: strict schema/field limits, omission of runtime/source/target fields, exact scope mappings, review/source hash rejection, settings preservation and independent stopped imports. REST/MCP exercise export/preview/import and JSON artifact save; a generated GUI fixture renders the mapping/review window.
- Watch source rebinding: saved-declaration fingerprints, explicit unavailable/partial baselines, stopped/revision/review guards and same-list updates with immutable source artifacts. Generated reads verify retained samples/definitions/settings across restart; paused recordings retain a forced cutover event. REST/MCP and generated GUI checks use no controller.
- Extension package hashes/contracts, generation pinning, reload over the same MCP connection, old-job completion, rejected reloads and rollback.
- Saved comparison sessions: restart persistence, exact ladder bookmarks, snapshot drift/unavailability and revision-controlled updates/deletion. Recovery previews are read-only, recheck session/snapshot/review hashes, reject conflicting applies and clear previous bookmarks/pages. Merge mappings: strict portable name-pair JSON round trips, new-definition imports, revision-bound export, guarded artifact save, explicit scope bindings, fresh value previews and preserved originals. REST/MCP share these operations; generated GUI workflows exercise recovery, import/export and editor concurrency.
- Comparison reports: all matching differences beyond visible pages, default omission of source paths/XML, HTML/source escaping, generated passive SVG and invalid-rung diagnostics, explicit snippet truncation, change/drawing/byte limits, stale hash rejection before publication, restart/project-change persistence, searchable history, artifact tamper rejection and forgetting without deleting artifacts/projects. REST/MCP exercise both formats and guarded report save. GUI workflows cover report options, changed-filter rejection, unchanged-snapshot navigation and history export/forget.
- Comparison annotations: report-derived change IDs, restart/project-change persistence, default reset, status/note search, cross-report ID rejection, compare-and-swap concurrency, atomic failed batches, per-note/batch/state limits, report/catalog/state tamper rejection, notes export revision checks and immutable original reports. Legacy JSON uses recorded contents; legacy HTML without catalogs fails with an explicit migration diagnostic. REST/MCP share progress/notes and verify separate exported artifacts.

`scripts/Verify.ps1` instead uses the configured running service and conditionally exercises its native bridge. Choose the isolated script for ordinary development without native execution.

Add `-WithCip` to isolated verification for the optional symbolic reader's generated read/reconstruction self-test, native library load/version check and rejected-startup protocol test. No tag handle or controller connection is created. The [CIP reader guide](CIP_READS.md) describes this offline evidence and the pending live acceptance boundary.

The generated `value-merge-workflow` GUI check exercises actual shared-service calls through the form: 310-tag discovery, selections across pages/searches, a combined preview/apply, preserved unselected values/source revision, scope-change invalidation and saved mapping binding. `comparison-workflow` navigates a late modified rung and an insertion gap, saves/restores its exact position, then previews recovery to a replacement snapshot, rejects changed filters and saves with the old bookmark cleared. `merge-mappings-workflow` checks editor save/load/update/delete, revision conflicts, portable artifact export/import and rejection of nonportable fields. Each emits a PNG plus JSON check list under the isolated results folder; none calls a native bridge or controller. Other render fixtures primarily check layout.

The comparison workflow also exports a JSON review with XML/drawings and reopens its recorded snapshot pair. `comparison-history-workflow` searches a newly exported report, saves a hash-verified HTML copy, rejects overwriting that file and forgets only the history entry while retaining its artifact. The separate history render fixture checks metadata layout.

The ladder browser ignores duplicate selection notifications for its already loaded routine, including delayed notifications when the hidden tree's native handle is created. Comparison workflow failures report the change count, displayed rung count and ladder diagnostic to distinguish a load failure from a layout/timing issue.

`comparison-annotations-workflow` retains drafts across pages, saves progress, searches notes by state/text, rejects an old edit revision even after refreshing, preserves the conflicting draft, explicitly discards/reloads it and exports a verified notes snapshot. Its render fixture checks the pending-state editor layout. These workflows use generated projects and the isolated shared service with controller access disabled.

`diagnostics-workflow` opens each of the 22 new operation editors from its workflow panel and checks the populated arguments against the live schema. `trend-views-workflow` creates/updates/loads a view, gathers a stopped recording, exports verified results and removes the view while preserving the stopped watch. Both use the isolated shared service and emit PNG/JSON evidence.

`documentation-workflow` exercises staged language edits, preview/apply, Unicode readback, retained drafts after another client's edit, explicit discard/reload and hash-verified full text export. It uses the generated multilingual fixture and emits PNG/JSON evidence. These checks do not validate native Studio/SDK localization import; see [documentation boundaries](PROJECT_DOCUMENTATION.md).

`settings-workflow` exercises the GUI broad-local preset, target policy clearing, saved-versus-running options and restoring the generated configuration. REST checks cover exact backups, competing saves, stale revisions and validation failures. The separate settings-staging fixture verifies that the administrative command opens no workspace or native session, then checks explicit empty roots on a temporary offline host. See [settings and access](SETTINGS.md).

## Optional app-owned framework

The optional Python [framework profile](FRAMEWORK.md#isolated-validation-and-limits) uses a separately installed, pinned wheel environment and the actual C# MCP adapter against an isolated service. It checks complete definitions/results, two frontends, immutable publication replacement, revision/write policy and app-error reconciliation without native providers. It is separate from the default .NET suite and does not migrate installed plugins.

## Optional offline Logix SDK checks

`scripts/Verify-ValueMergeNative.ps1` publishes a separate native bridge/verifier, merges generated v38 scalar/STRING/nested UDT/UDT-array values, exports a new ACD, reopens/builds it in a fresh offline SDK job and compares native re-exported values. It disables controller operations and configures no targets; the normal service is untouched. Run evidence stays under ignored `artifacts/test-results/value-merge-native-*`. This checks the generated fixture and installed SDK, not live upload or general native/runtime acceptance.

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
