# Program-local online test

`CodexTest-main-rungs.L5X` is a prepared arithmetic test, not a controller-validated export. It adds one rung and three new base tags in `CodexTest`, with no controller tags, aliases, parameters, I/O mapping or external routine calls. `CodexSdk_Enable` starts false; while enabled, the rung calculates `CodexSdk_Result = CodexSdk_Input + 1`.

Before dispatch, use a preserved, matching ACD and fresh export to verify that `CodexTest` is scheduled, `main` is its main routine, the routine is empty, these names are unused throughout the project, and no unrelated program edits exist. Do not overwrite an occupied routine or reuse existing tags. Bind the approved controller context and hashes, prepare a `rungs` / `accept_for_test` plan with insert position 0 and replace count 0, then submit through the shared service. Test on a copy through the native SDK before the live import; version 35 does not support this installation's BuildAsync.

The user operates Studio during this validation. Ask for these observations at each stage; do not mark them observed from SDK success:

1. After import, verify one accepted rung in `CodexTest.main`, three new local base tags and no other program edits.
2. The API sets Input to 41 and Enable true. Before Test Edits, Result should remain -1.
3. Ask the user to Test Accepted Program Edits for `CodexTest`. Check Result becomes 42. Set Input to 99 and check Result becomes 100.
4. For an Untest check, first disable the rung. Ask the user to Untest `CodexTest`. Set Result to -123, then Enable true; Result must remain -123. Ask the user to Test again; Result must become 100.
5. Ask the user to assemble only `main`'s accepted edits after successful checks and confirm the edit state clears. Verify Input 123 produces Result 124 after assembly.
6. Set Enable false, retain the final native ACD/export and hashes, and record the user's native observations separately from API value checks.

Keep all values small to avoid overflow. If a native state or numeric result differs, stop the dependent sequence and investigate. Do not download, change controller mode, force I/O or modify safety settings to make the test succeed.
