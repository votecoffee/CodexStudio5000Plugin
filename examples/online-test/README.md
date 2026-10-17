# Program-local arithmetic example

[ExampleProgram-MainRoutine-rungs.L5X](ExampleProgram-MainRoutine-rungs.L5X) is a generated one-rung import fixture, not a native-validated export or an approved controller change. It uses the placeholder program `ExampleProgram`, routine `MainRoutine` and three program-local base tags. `Example_Enable` starts false; while enabled, the rung calculates `Example_Result = Example_Input + 1`. It has no controller tags, aliases, parameters, I/O mapping or external routine calls.

## Prepare a disposable test

Use a preserved offline project copy first. Confirm the program is scheduled, the named routine is its empty main routine, the tag names are unused and there are no unrelated program edits. Adapt the placeholder context to the authorized test fixture; do not import over an occupied routine or reuse existing tags.

Bind source/import hashes and exact program/routine scope through the [online-edit planner](../../docs/ONLINE_EDITS.md). An insert into an empty routine uses `rungs`, insert position 0 and replace count 0. Check the native exported rung count, logic and local tag definitions after an offline import. Build support depends on the installed Logix version.

## Optional authorized live validation

A live run requires a separately authorized target/action and functional criteria. Stop dependent steps if native state or numeric results differ. Record observations locally, including whether Studio is operated manually or through an authorized agent workflow.

For an `accept_for_test` plan:

1. After import, inspect one accepted rung, the three new local base tags and no unrelated program edits.
2. Set Input to 41 and Enable true. Before native Test Edits, Result should remain its initial -1.
3. Test the selected program's accepted edits in Studio. Check Result becomes 42, then set Input to 99 and check Result becomes 100.
4. For an optional Untest check, disable the rung, untest the selected program, set Result to -123 and enable again. Result should remain -123 until testing resumes, then become 100.
5. After successful functional checks, assemble only the authorized scope, confirm its edits clear and check Input 123 produces Result 124.
6. Disable the test and retain a new native ACD/export plus the observed edit states.

`finalize_during_import` is a separate initial-import workflow that provides no native test interval; do not report it as Test/Untest coverage. Keep values small to avoid overflow. Do not download, change mode, force I/O or modify safety settings to make a check pass. No live action is implied by this example.
