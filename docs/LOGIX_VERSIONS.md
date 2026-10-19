# Logix Designer version support

**Studio 5000 Logix Designer v38 is supported by the existing shared service, REST/MCP operations and desktop SDK jobs.** It does not require a separate MCP adapter or plugin. Logix Designer's major revision is separate from the SDK package version: the native bridge binds `RockwellAutomation.LogixDesigner.CSClient` **2.1.974**.

| Path | v38 scope |
|---|---|
| Saved L5X | Register, inspect, hash-bound preview/apply, export and structural comparison preserve revision metadata and XML properties. |
| Browsing and recording | Declared tags/UDTs, arrays and saved decorated values use the same browser, watch and recording operations. Native online polling still requires an authorized configured target. |
| References and ladder | Saved RLL/ST lexical where-used, passive ladder previews and explicit-context ME/SE reference resolution use the same shared operations. |
| Native SDK | Explicit revision-38 project creation/conversion, ACD/L5X open/save, build and supported SDK operations use the installed v38 engine. Operation/controller/hardware limitations still apply. |

The SDK GUI's revision argument examples and `Verify-Sdk.ps1` already default to **38**. For API creation, use `CreateNewProjectAsync` with `majorRevision: 38`; processor enumeration uses `majorRev: 38`, and revision conversion uses `destinationRevision: 38`. See [SDK request examples](SDK.md). Opening a saved project uses its stored revision. Editing L5X does not upgrade controller firmware or convert an ACD to another revision; those are separate explicit native operations.

## Dependencies and boundaries

Install a licensed Logix Designer SDK and compatible **Logix Designer v38** components to use native v38 operations. SDK binding coverage checks alone do not establish that the v38 engine is installed or licensed. XML-only reads/edits do not require that native engine.

Availability is per operation, project type, installed release and hardware. Consult the installed SDK's `Documentation/LDReleaseLimitations.html` and operation-specific limitations. For example, the bound package documents Build support from Logix Designer 37; basic older-project opening does not imply every SDK operation is supported on that older version. A rejected native operation retains its actual SDK diagnostic.

Existing coverage boundaries apply to v38: structural validation is distinct from native build/runtime validity; missing type templates are not invented; lexical where-used is not a compiler binding result; and the legacy exploded layout rejects unsupported protected AOI/ST metadata. See [exploded compatibility](EXPLODED_PROJECTS.md), [tag where-used](TAG_REFERENCES.md) and [validation](VALIDATION.md). No blanket claim is made that every newer firmware feature has a dedicated high-level operation. Raw/typed SDK capabilities remain limited to the bound SDK surface.

## Repeatable v38 checks

Generated v38 regression checks use [WatchDemo.L5X](../examples/WatchDemo.L5X) for revision preservation, managed edits/exports, UDT/array browsing, unsigned/LREAL values and reference/ladder operations:

```powershell
dotnet test tests/Studio5000.Tests/Studio5000.Tests.csproj -c Release --filter FullyQualifiedName~Logix38CompatibilityTests
```

For native offline validation with the installed licensed SDK/engine:

```powershell
.\scripts\Verify-LogixVersion.ps1 -Revision 38
```

This publishes a separate current bridge/verifier under ignored `artifacts/version-validation/`. An isolated shared-service instance has **controller operations disabled and no configured targets**. It creates a generated project, builds/saves it, adds an unscheduled program with local DINT/LREAL/ULINT tags, converts that edited L5X to a new ACD, checks exact offline tag round trips, saves/reopens in a fresh SDK job and checks baseline preservation. Native L5X artifacts must report the requested software/controller major revision. All requested tag access explicitly uses Offline, no target/path is configured, and the job requests contain no go-online, transfer or controller-mode calls. It does not use the normal running service or its configuration.

Reports and generated files remain local under `artifacts/test-results/logix-v38-<run-id>/`; the report includes the requested major revision, bridge hash, job outcomes and the precise offline boundary. A failure retains evidence and does not automatically retry. This focused verifier includes Build and accepts revision arguments from 37 upward; an accepted argument is not proof that the requested engine exists or that every fixture type is supported there. v38 is the explicit default.

The broader `Verify-Sdk.ps1 -Revision 38` additionally covers partial imports/exports, conversion/controller type changes and offline safety fixtures. It uses the configured service and is an explicit native verification action; read [VALIDATION.md](VALIDATION.md) before choosing that workflow. Its summaries record `logixMajorRevision` alongside operation results. Neither verifier establishes live v38 editing, physical I/O, HMI, safety commissioning or runtime acceptance.
