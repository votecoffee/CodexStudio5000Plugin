# Native-local candidate and pilot

The `local-project` execution profile enables a bounded existing SDK path for disposable offline project reads, exports, imports and builds. It uses shared Core/service and the existing exact owned native-worker/session adapters. Preparing a candidate does not authorize changing an installed deployment or connecting a controller.

## Boundaries

The profile requires restricted file roots, no controller targets or communications paths, controller access disabled, no CIP bridge, SDK service recovery/automatic preflight disabled, and native GUI launch disabled. Settings validation refuses conflicting flags; a broad-local preset cannot silently widen this profile. GUI Settings exposes the profile and GUI handoff switch. Saved settings remain staged until restart.

Core requires the generated candidate's exact fixture/export/state/worker paths, so inherited array entries and mixed installed workers cannot widen it. Candidate startup removes inherited engineering/Kestrel/ASP.NET transport overrides for the child launch, then restores the caller's environment. It never changes the installed service's settings or process environment.

The SDK catalog publishes exact `localProjectMethods`: project open/create/conversion/save, processor metadata, partial XML export, **offline `PartialImportFromXmlFileAsync`**, builds and explicit `Offline` tag reads. Controller/communications, live watches, target/rung imports, tag writes and safety methods are refused before launch. The worker independently checks its profile against the owner's launch environment and applies the same call policy. Unexpected connected state refuses local work without a forced GoOffline action. Disposing its own SDK connection does not resolve incomplete save/effect evidence.

The SDK Windows service remains SCM-owned. This profile sends no app SCM start/stop/reconfiguration request and never adopts or terminates it. It must already be running for the native pilot; vendor behavior still needs actual native acceptance. SCM recovery, native GUI/HMI work and real controllers need separate configuration, authorization and acceptance. Export-file HMI operations retain their existing independent capabilities.

## Prepare without activation

```powershell
.\scripts\Prepare-NativeLocalCandidate.ps1
```

Preparation compiles into a **new** versioned destination under ignored `artifacts/native-local-candidates/`. It never runs the normal installer, replaces installed binaries, selects normal extension pointers, touches Codex configuration, starts an engineering service or executes vendor SDK operations. It invokes only the candidate service's pure `--native-local-settings <absolute-workspace>` command for Core-generated configuration. Proprietary dependencies remain local; existing SDK/framework pins are unchanged.

The candidate contains service/MCP/GUI/extension-worker/native-bridge binaries, four one-worker extension packages, a generated fixture, explicit configuration, launch/stop scripts and a SHA-256 payload manifest. The manifest records source commit/dirty state. Incomplete builds remain with a failure record. A prepared candidate retains `nativeAcceptance=pending` after compilation and harmless tests.

Independent review must pin the exact manifest hash, file hashes and source commit. Do not edit a prepared destination to upgrade it. The launch script checks the reviewed manifest hash and payloads, refuses changed boundaries/reparse points/shared-host selection, and selects a dedicated resource registry under the candidate's `.local/`. It shares neither registry nor state with the installed service. Port defaults to 5488; choose another unused loopback port during preparation if needed.

## Harmless adapter verification

```powershell
.\scripts\Verify-NativeLocalProfile.ps1
```

Two real bounded private Windows workers use a Core-based stand-in, without vendor assemblies. An SCM driver throws if accessed. Success requires exact close evidence, terminal private-worker receipt, released native lease and owner-close receipt. The deliberately incomplete case must return `indeterminate`, retain its barrier and refuse shutdown without a false closed receipt. Keep those fixture records. This is adapter verification, not native SDK acceptance.

## Smallest native pilot — separately authorized

1. Preserve the installed offline deployment. Review candidate/fixture hashes, target version/processor and dedicated roots/registry. Confirm SDK licensing/version availability and an already-running SDK Windows service; do not start/stop it within this pilot.
2. Start only the candidate from its directory:

   ```powershell
   .\scripts\Start-NativeLocalCandidate.ps1 -ExpectedManifestSha256 <reviewed-sha256> -Background
   ```

3. Use that candidate's authenticated loopback API. Read health, effective settings, SDK and extension catalogs. Confirm `local-project`, no targets and disabled controller/SCM/GUI flags. No plugin installation or desktop reconnect is required for an independently reviewed API pilot; this grants no service switch for unrelated work.
4. Submit one SDK job creating an empty disposable v35 `1756-L81E` project:

   ```json
   {"calls":[
     {"method":"CreateNewProjectAsync","arguments":{"majorRevision":35,"processorTypeName":"1756-L81E","controllerName":"NativeLocalPilot"}},
     {"method":"BuildAsync","arguments":{"target":"DefaultTarget"}}
   ]}
   ```

   Retain its job ID, diagnostics and terminal result. Review logged build errors and expected output content; a returned API/job status alone is insufficient acceptance. Core remaps filenames into a private job, publishes final ACD/L5X artifacts and a managed result project. There is no controller download or runtime scan.
5. Review `fixtures/NativeLocalProgram.L5X` and its manifest hash. Submit a new offline job against the created `projectId` and exact `expectedSha256`, with `inputFiles.probe={path:<absolute-fixture-path>,sha256:<exact-hash>}` and these calls:

   ```json
   [
     {"method":"PartialImportFromXmlFileAsync","arguments":{"xPath":"Controller/Programs","xmlFileToImport":"probe","collisionOption":"CancelOnColl","continueOnErrors":false}},
     {"method":"BuildAsync","arguments":{"target":"DefaultTarget"}},
     {"method":"GetTagValueDINTAsync","arguments":{"tagPath":"Controller/Programs/Program[@Name='NativeLocalProbe']/Tags/Tag[@Name='PilotInput']","mode":"Offline"}},
     {"method":"PartialExportToXmlFileAsync","arguments":{"xPath":"Controller/Programs/Program[@Name='NativeLocalProbe']","filePath":"probe-readback.l5x"}}
   ]
   ```

   Expect saved `PilotInput=41` and `PilotResult=0`; the unscheduled rung has not run. The generated XML has structural checks only until actual native import succeeds. Retain exact program/rung/tag readback and hashes; an import return code does not prove preservation.
6. Reopen the saved ACD artifact in a separate offline SDK job using its retained artifact ID/hash. Repeat read/build/export and compare against the reviewed result. Preserve originals and all step/lifecycle/resource receipts. Check exact worker exit, native lease release and normal candidate owner close through its authenticated stop script. SCM and other applications must remain undisturbed.

Failed/indeterminate steps, lost replies, unexpected connections, unproven close or admission/query errors stop that path. Inspect the **same** retained operation; never replay it or clear barriers to finish. Process exit does not prove rollback or native close. Acceptance must identify actual operations, SDK/Logix version, fixture, hashes and readback scope. GUI/HMI and controller/monitoring pilots remain separate.

The standalone app-owned adapters are used directly. Optional shared-host/provider migration and universal plugin deployment are not prerequisites. Framework ledger contention and unknown retained histories are not repaired automatically by this profile.
