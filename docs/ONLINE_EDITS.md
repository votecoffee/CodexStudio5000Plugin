# Online edits, testing and finalization

The installed SDK 2.1.974 supports online partial imports with three dispositions:

| Option | Meaning | Test interval |
|---|---|---|
| LeaveEdits | Leave imported logic pending | Not started |
| AcceptEdits | Accept and send imported changes | Native Test/Untest needed |
| FinalizeEdits | Accept/send; also assemble when the controller is in Run mode | None provided |

There is no standalone SDK Test Edits, Untest Edits or Assemble Edits API. `ChangeControllerModeAsync(Test)` changes controller mode and does not test accepted logic edits. A build does not test controller behavior either. The plugin exposes SDK import/accept/finalize and a separate native Studio workflow for Test/Untest/Assemble. See [VALIDATION.md](VALIDATION.md) for verification procedures and the separate native/controller boundaries.

## Shared planning operation

Call `POST /api/sdk/online-edits/plan`, or MCP `studio_online_edit_plan` with the body below as its `request` argument:

```json
{
  "controllerTargetId": "authorized-lab-plc",
  "programName": "MainProgram",
  "routineName": "MainRoutine",
  "sourcePath": "C:\\Projects\\CurrentController.acd",
  "expectedSha256": "ACD_SHA256",
  "importFile": { "path": "C:\\Exports\\EditedRungs.l5x", "sha256": "XML_SHA256" },
  "importKind": "rungs",
  "insertPosition": 0,
  "replaceCount": 1,
  "workflow": "accept_for_test"
}
```

Read hashes with `studio_sdk_inspect_source`. Use `sourceArtifactId` instead of `sourcePath` for a native ACD artifact, and `importFile.artifactId` instead of its path for an exported XML artifact. External files require allowed roots. Names identify one program/routine, not XPath selectors. Rung positions/counts apply only to `rungs`; `routine` uses the with-target import for the named routine.

Preparation validates the target, ACD source, both hashes and Logix XML without opening a native project. It returns `planSha256`, `sdkRequest`, Studio handoff steps and false test/finalization observations. It works with controller execution disabled. It does not connect, enable execution, start a job or verify controller correlation. Inputs are checked again at actual job submission.

Preparation also applies the shared bounded RLL branch-grammar check to readable rung text. Every bracket branch needs at least two nonempty legs and a comma at its own depth; operand commas and array brackets inside instruction parentheses do not count. The legacy BST/NXB/BND notation has the same nonempty two-leg requirement. For example, `[[XIC(A)XIC(B)],XIC(C)]OTE(D);` is rejected because its inner bracket group has one leg. `[XIC(A)XIC(B),XIC(C)]OTE(D);` passes this branch-shape check. The checker never rewrites the input.

Schema-valid XML can still contain invalid ladder text. The `rll-branch-grammar` check is also included in named Logix validation profiles and the ladder browser/evaluator. Inherited malformed branches still fail; native expected-difference rules cannot waive them. These checks do not establish instruction validity, native compiler/import acceptance or controller behavior.

The generated sequence is GoOnline → ReadConnectedState → ReadControllerMode → partial import → Save. It contains no download, mode change, force, safety unlock or signature change. A correlation failure is not repaired automatically by downloading. `PartialImportFromXmlFileAsync` is offline-only and is rejected after a requested GoOnline unless the sequence goes offline first.

Submit `sdkRequest` through the existing SDK job endpoint/tool only within actual authorization for that target/action, with controller execution enabled and the target configured. The SDK session lasts one job, then disconnects/disposes. Inspect all attempted calls and outputs; plans do not execute themselves.

The desktop **SDK operations… → Online edit…** dialog uses the same service. It defaults to `accept_for_test`, puts the generated request in the job editor and displays the plan separately. Run is a separate action. With no target configured, the dialog explains how to configure the intended target while keeping controller execution disabled for preparation.

## Native lifecycle

### Routine replacement targeting

Supplying an existing routine XPath to PartialImportWithTarget can append RLL content instead of replacing the routine. The planner uses the parent program XPath plus the exact routine target name for `routine` imports. Scoped policies require that parent, exact target name and a matching Routine-target XML payload; they reject the append-prone routine path. Rung imports and partial exports still address the exact routine XPath.

Core and REST/MCP regressions cover the mapping and rejected broader targets. The SDK verification script checks the exported rung count and expected logic after partial imports; a successful import/build alone cannot detect an appended duplicate. Verify these postconditions on a preserved offline copy before an authorized live import.

`accept_for_test` selects SDK `AcceptEdits`. Save its final ACD artifact as a new file, open it in Studio, confirm the controller/path, reconnect within scope and inspect accepted edits. Use native Test, the user's functional checks and then Assemble. Testing can affect other edited routines in the same program, so inspect that scope first. Prefer routine-scoped assembly when supported and sufficient, otherwise use the authorized program scope.

`leave_pending` selects `LeaveEdits` and requires native review/acceptance before testing. Existing accepted edits can go directly to Studio inspection without reimporting.

`finalize_during_import` selects SDK `FinalizeEdits` and skips the separate test interval. It does not imply functional testing. Do not choose it to bypass a missing Test command or change controller mode to force assembly. The documented assembly condition is Run mode; SDK completion does not independently prove the resulting edit state.

Choose that option on the **initial import** when API-only finalization is intended. An existing accepted edit in a saved ACD or controller can block the next import with `XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM`. Do not reimport, append another rung or cancel accepted edits to work around it. Finish the authorized existing edits in Studio. The shared service adds a `native_action_required` event for this SDK diagnostic while retaining the original failure and attempted calls.

Use the bundled [logix-online-edits skill](../skills/logix-online-edits/SKILL.md) for the native phase. The user can operate Studio and report its resulting states while the agent uses the API. Honor requests to leave the UI hands-off; label native user reports separately from API measurements. Use Windows Computer Use only within the user's current authorization. The service implements neither terminal-driven UI input nor undocumented edit-lifecycle calls.

Report separately: plan prepared; SDK import returned; native Testing Edits observed; functional checks passed; native assembly observed; saved/re-exported result retained. File equality or job success cannot authenticate those native states. Interrupted/indeterminate work is not automatically retried; cancellation is not rollback.

## Local verification and later validation

Core checks cover option mapping, hashes, exact scope, XML entities, unsupported SDK sequences and absent fake test evidence. REST/MCP tests use an isolated local service with opaque fixture files, an inert bridge and controller execution disabled. They validate plans across both transports, reject stale hashes and blocked execution, and create no native job. The desktop dialog renders off screen.

Later live validation needs an explicitly chosen controller/Echo instance, matching ACD, authorized edit and functional criteria. Verify the import and native lifecycle on that actual version before claiming end-to-end behavior.

Primary references: installed SDK `LogixProject.PartialImportOption` documentation; Rockwell [Test Accepted Program/Phase Edits](https://www.rockwellautomation.com/en-us/docs/studio-5000-logix-designer/38-01/contents-ditamap/studio-5000-logix-designer/navigating-the-application/-product_name_rsl5k--application-main-window/-product_name_rsl5k--application-commands/test-accepted-program-phase-edits-command.html), [Assemble Accepted Routine Edits](https://www.rockwellautomation.com/en-us/docs/studio-5000-logix-designer/38-01/contents-ditamap/studio-5000-logix-designer/navigating-the-application/-product_name_rsl5k--application-main-window/-product_name_rsl5k--application-commands/assemble-accepted-routine-edits-command.html), and [Logic menu](https://www.rockwellautomation.com/en-us/docs/studio-5000-logix-designer/38-01/contents-ditamap/studio-5000-logix-designer/logic-menu.html).
