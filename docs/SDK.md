# Logix Designer SDK operations

Controller access, named target restrictions and explicit `path:` target policy are editable through **Settings…**; see [settings and local access](SETTINGS.md). Defaults remain restricted. Saving settings stages them for the next service start.

All **59 operation names in RockwellAutomation.LogixDesigner.CSClient 2.1.974** are exposed through native jobs. This is version-specific: it excludes APIs added by newer packages, and each operation still depends on the installed Logix version and project/controller support.

**Logix Designer v38 is supported** with the installed compatible engine; project creation/conversion explicitly accepts revision 38, and the desktop examples already default to it. See [version support, dependencies and the isolated offline verifier](LOGIX_VERSIONS.md). SDK package version 2.1.974 and Logix major revision 38 identify different components.

The frozen API has **67 public signatures, 63 unique method names**: 59 domain operations, four internally managed event/disposal methods, and four alternative enumerable-logger overloads. Generated direct C# calls compile against the vendor package. `/api/sdk/coverage` compares actual signatures and inspects compiled IL, reporting both `matchesFrozenSurface` and `allOperationsBound`. Runtime dispatch uses an explicit switch; requests cannot invoke arbitrary methods or upload executable callbacks. The alternate logger overloads use the bridge's event collector. `RequestedUploadMergeMode` is an enum without a public method parameter in this version; the catalog preserves it without inventing an API.

| Family | Operations |
|---|---|
| Projects | Open/create/save/SaveAs; detailed L5X/L5K; revision conversion; controller-type conversion; processor catalog |
| Builds | Default, physical-controller and Echo targets |
| Components | Partial export; collision-aware import; import with target; rung insertion/replacement and edit disposition |
| Tag values | Raw bytes and BOOL/SINT/INT/DINT/LINT/USINT/UINT/UDINT/ULINT/REAL/LREAL/STRING; offline and online |
| Communications | Get/set path, connection state, go online/offline |
| Transfers/modes | Download, upload, upload to new project; read mode; request Run/Program/Test |
| Safety | Network number, lock state, lock/unlock/password changes, get/generate/delete signature |
| SD card | Store/load image, load event/mode, firmware-update setting, image name/note |
| Lifecycle | Collected/redacted status/error/progress events, handler lifecycle and disposal |

## Shared API and GUI

Read `studio_sdk_catalog` / `GET /api/sdk/capabilities` for exact methods, parameters and enum names. SDK XPath strings start at `Controller/...`; XML editor selectors normally start at `/RSLogix5000Content/...`.

`POST /api/sdk/jobs` starts a job. Calls execute in order in one native project session. Supply one source (`projectId`, `sourcePath` or `sourceArtifactId`) with its exact `expectedSha256`, or start with `CreateNewProjectAsync` / `UploadToNewProjectAsync`. Factories must come first. Static `GetProcessorTypesAsync` needs no source. Opening a source can be implicit or an explicit first `OpenLogixProjectAsync` call.

```json
{
  "calls": [
    { "method": "CreateNewProjectAsync", "arguments": {
      "majorRevision": 38, "processorTypeName": "1756-L81E", "controllerName": "Demo"
    } },
    { "method": "BuildAsync", "arguments": { "target": "DefaultTarget" } },
    { "method": "SaveAsAsync", "arguments": { "savePath": "detailed.l5x", "detailedL5x": true } }
  ]
}
```

Poll `GET /api/sdk/jobs/{id}` / `studio_sdk_get_job`. Results contain status (`queued`, `running`, `succeeded`, `failed`, `cancelled`, `indeterminate`), per-call values, diagnostics, artifacts and a new managed result project. Source files/revisions/artifacts remain preserved. A successful project session produces final ACD/L5X artifacts and explicitly requested exports. Use `studio_save_artifact` to save a new file under `OutputRoots`. Native XML over the managed editor's 64 MiB limit remains available as artifacts for SDK jobs without editor adoption. SDK source inputs support up to 500 MiB.

`GET /api/sdk/jobs` lists the last 100 jobs. `POST /api/sdk/jobs/{id}/cancel` requests cancellation; poll its terminal status. Client disconnection does not cancel submitted work. Cancellation is not rollback. Timeout, child-process termination or a service restart can leave effects indeterminate; inspect any external target before retrying. The service never automatically retries. Per-call results remain relevant if a later call or final publication fails.

Native steps/events are currently buffered until the helper returns, then published with its result. A running job can therefore have empty steps/events/artifacts throughout a lengthy open/export. Empty lists do not establish failure, a stall or progress within a specific call. Do not restart/replay a job merely because those lists are empty.

`studio_sdk_inspect_source` / `POST /api/sdk/source` with `{ "path": "C:\\Projects\\Machine.acd" }` reads a source hash without opening it in the SDK. Jobs snapshot and rehash inputs again, rejecting stale hashes. `SaveAsync` saves the private copy. Source ACD/L5X/L5K and external XML files require an allowed root; managed artifact inputs are verified in storage.

Output `savePath` / partial-export `filePath` values are simple new filenames, remapped into the job directory. SaveAs accepts ACD/L5X/L5K; partial export accepts XML/L5X. Overwrite mode, traversal, duplicate names and reserved input/import/created/final names are rejected. Factory paths are injected: omit `projectFilePath` and conversion `projectPath`.

Partial imports use aliases bound to a path or previous artifact plus its exact hash:

```json
{
  "sourceArtifactId": "ACD_ARTIFACT_ID",
  "expectedSha256": "ACD_SHA256",
  "inputFiles": {
    "program": { "artifactId": "PARTIAL_L5X_ID", "sha256": "PARTIAL_L5X_SHA256" }
  },
  "calls": [{ "method": "PartialImportFromXmlFileAsync", "arguments": {
    "xPath": "Controller/Programs/Program[@Name='MainProgram']",
    "xmlFileToImport": "program", "collisionOption": "OverwriteOnColl"
  } }]
}
```

An external import instead uses `{ "path": "C:\\Exports\\Program.l5x", "sha256": "..." }`. XML DTDs/external entities are rejected. Enum arguments are exact string names. Typed values are JSON scalars with SDK range checks; use an integer-capable client for exact 64-bit values. Raw `tagValue` is base64, maximum 30000 decoded bytes. Raw reads return `{ "base64": "...", "bytes": n }`; the bridge does not guess byte order/layouts.

The **SDK operations…** GUI uses this same service. Add methods from its catalog, choose a source or the selected managed project, edit JSON arguments, run, refresh and save an output. The catalog shows configured targets. Password arguments remain in memory/stdin; the raw request is not persisted. Collected diagnostics redact supplied nonempty passwords, and the GUI clears them after submission. Vendor diagnostics outside this application have independent behavior.

## Controller targets

For online rung/routine changes, use the [online-edit planner and native lifecycle workflow](ONLINE_EDITS.md). The installed SDK has no standalone Test/Untest/Assemble methods; controller Test mode is not Test Edits. The default plan accepts imports for later native testing. Finalizing during import is an explicit alternative with no test interval. Planning does not connect or enable a target.

Controller APIs are implemented but disabled by default. Configure an authorized target in ignored `service.local.json`, restart, then select its `controllerTargetId`:

```json
{
  "Studio5000": {
    "EnableControllerOperations": true,
    "ControllerTargets": [{ "Id": "lab-plc", "CommunicationsPath": "EXACT_FACTORYTALK_LINX_PATH" }]
  }
}
```

The service injects the selected target's path, preventing request overrides. When `AllowAnyControllerPath` is enabled, `controllerTargetId: "path:<exact SDK communications path>"` can also select an explicit route without a saved alias. Any `mode=Online`, go-online, download/upload, controller mode, SD-card or generate/delete-signature call requires controller operations enabled and an explicit target. Setting a path on an offline copy requires a named or allowed `path:` target but does not connect or require live operations enabled. Sessions end when the job disconnects/disposes.

A target can additionally define `Policy` to limit every submitted method, exact program tag addresses and component definitions. This applies to REST, MCP and the desktop through the shared service, including offline calls submitted with that target. Omitted policies retain the existing unrestricted configured-target behavior; an empty `AllowedMethods` denies all calls.

```json
{
  "Id": "scoped-test",
  "CommunicationsPath": "EXACT_FACTORYTALK_LINX_PATH",
  "Policy": {
    "AllowedMethods": ["UploadToNewProjectAsync", "ReadConnectedStateAsync", "ReadControllerModeAsync", "GoOnlineAsync", "GetTagValueDINTAsync", "SetTagValueDINTAsync", "GetTagValueBOOLAsync", "SetTagValueBOOLAsync", "PartialImportRungsFromXmlFileAsync", "SaveAsync"],
    "ProgramName": "ExampleProgram",
    "RoutineName": "MainRoutine"
  }
}
```

Program policies accept exact base tag paths such as `Controller/Programs/Program[@Name='ExampleProgram']/Tags/Tag[@Name='Example_Input']`; controller tags, other programs, member/array selectors and XPath expressions are denied. Component operations require the exact configured ladder routine. Imports reject global definitions, other routines, aliases, program parameters and produced/consumed tags; the built-in scalar context type entries emitted by native exports are allowed. Program policies also reject download, mode changes, safety changes and SD-card operations even if accidentally placed in the method list. The bridge's internal open, path setup, disconnect and output saves remain infrastructure operations.

These checks bound explicit addresses and imported definitions. They do not interpret ladder operands or prove that arbitrary code cannot reference existing global data, or that an existing local tag is not an alias. Review instruction operands and source tag definitions before dispatch; use newly created base local tags for an isolated test.

The agent needs explicit user authorization for the actual target/action. Offline development tests do not authorize actions on a plant controller. The bridge does not automatically change mode, download, remove a signature or unlock safety to make another operation succeed.

## Verification and support limits

`scripts/Verify-Sdk.ps1` uses generated version-38 standard/GuardLogix fixtures for builds, typed/raw round trips, partial exports/imports, conversion and offline safety. Its isolated path fixture writes a TEST-NET address into a copy with controller operations disabled and never calls a connection API. Reports retain hashes, attempted calls and diagnostics.

Use the coverage endpoint to compare all 67 frozen signatures and confirm the 59 operation bindings against the actual installed package. Binding coverage is distinct from executing every operation, argument combination or Logix version. See [VALIDATION.md](VALIDATION.md) for reproducible offline checks and the separate live/native validation boundaries. Keep installation-specific results and controller observations local.

A fresh GuardLogix project has no signature: get-signature preserves `XMLSrv_E_NO_VALUE_TO_GET` as an expected negative case. Build support depends on the installed Logix version; the verifier defaults to version 38. An untouched native export/reimport control helps distinguish source compatibility failures from edit failures. Native call completion is distinct from native edit-state observation, functional behavior, physical I/O, safety and commissioning acceptance.

### Native handoff and version-dependent failures

Deliver a requested named edited L5X after its authorized edits and required scoped checks, with a new filename in the requested allowed folder and file/artifact hash readback. Respect user-owned Studio import. Do not append `BuildAsync` or another SDK import/build as an automatic delivery gate. ACD generation, native import/save/readback and Verify Controller are different operations and evidence layers; perform only the native work requested and supported for the selected installation.

API inventory and compiled binding coverage establish that a method exists in the installed SDK, not that a particular project, Logix engine revision or build target supports it. Retain exact SDK and Logix versions, input SHA, method/arguments, target, native error code/message and job/per-call status. For example, an `operation-not-supported` result from a `BuildAsync` attempt on v35.3 must be recorded for that attempted combination; it does not establish a universal v35 prohibition, invalidate the edited L5X or prove that Studio import is unavailable. Reuse the existing bounded result; do not rerun customer inputs or cycle through targets just to replace an unsupported outcome with a pass.

A requested cancellation must be followed to its retained terminal/per-call outcome. `cancelled` is not rollback, and earlier completed calls may have effects even if the job later fails. For timeout, termination, service restart or a lost response, reconcile the existing job/artifact/output identities; preserve `indeterminate` when completion is unknown. No automatic retry, rollback, edit-state clear or replacement job is authorized by uncertainty. See [the shared-service handoff workflow](../workflows/rockwell-engineering/references/structural-edits.md#deliver-the-named-l5x-then-honor-native-ownership).

Newer SDK content-protection APIs are outside this frozen installed surface. Add those after upgrading and reinventorying the actual package. See [Rockwell's SDK guide](https://literature.rockwellautomation.com/idc/groups/literature/documents/gr/ldsdk-gr001_-en-p.pdf); installed package documentation/examples define this version's signatures.
