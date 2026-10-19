# Merge PLC tag values into an offline project

The shared service transfers selected whole-tag values between two different registered Logix projects. A source can be an offline ACD/L5X or a saved online upload. Apply creates a new managed revision of the offline destination. It preserves both original files, the source revision, destination tag definitions, descriptions, ladder logic and force data. Export a separate L5X or ACD afterwards. It never downloads or writes values to a controller.

## Desktop workflow

1. Select **Merge tag values…** in the main window. Use **Open source…** and **Open destination…**, or select previously registered snapshots. Opening ACD files requires the configured native SDK converter; L5X does not.
2. Choose a source scope and destination scope. Controller scope is explicit; a program selection uses only its local tags. **Find matching tags** pairs exact tag names in those scopes and reports incompatible/missing destinations.
3. Select the rows to merge, then choose **Preview selected values**. Review before/after values, diagnostics and source/destination/review hashes. Displayed large values are abbreviated; the result hash binds the full output. Every selected row must be supported. Remove failed rows and preview again.
4. Choose **Apply reviewed offline merge**. The destination advances one revision; no other registered project changes. A changed source, destination, selection or review hash is rejected. Repeating an applied request fails the destination hash check.
5. Use **Export destination L5X…** or **Export destination ACD…** to save a new file under an allowed output root. Existing files cannot be overwritten. ACD export requires the native SDK. Native import/build and runtime acceptance remain separate checks.

The online source controls use an explicitly configured target. **Upload online source** queues an upload-only SDK job, then loads its completed project as the source. Controller operations must be enabled, the native bridge must be configured, and the target policy must allow `UploadToNewProjectAsync`. A program-scoped policy that excludes controller-wide uploads remains enforced. Capturing a source does not preview or apply a merge. No indeterminate/failed capture is retried automatically. **Cancel upload** requests cancellation; inspect the terminal job status. Closing this window leaves a shared job running; its ID can be loaded later with **Load upload job**.

Uploaded values are a point-in-time saved snapshot, not an atomic multi-tag read or an ongoing subscription. The report identifies `controller_upload_snapshot` and a capture timestamp only for the exact, unedited result of a successful upload-only job. Other projects are labeled `saved_project`.

## Supported values and compatibility

- Standard base tags only. Aliases, produced/consumed tags, safety tags/data types and types without complete exported UDT declarations are excluded.
- Exact declared type names, array dimensions and reachable UDT member layouts must match. Member order, hidden members and bit mappings remain significant. Names/scopes may be explicitly mapped; there is no inferred rename or numeric conversion.
- Supported numeric leaves are BOOL/BIT, signed/unsigned 8/16/32/64-bit integers, REAL and LREAL. Integer ranges/radix bit patterns and finite floating point values are checked. Nested UDTs and up to three-dimensional arrays need complete decorated data matching their declarations. Raw/L5K-only, unsupported scalar encodings and incomplete values are rejected.
- Scalar `STRING` uses a `Data Format="String"` representation with an exact declared length of at most 82 characters. Other string layouts/native built-ins need additional supported exported declarations/encodings; unsupported rows explain the failure.
- One merge contains 1–256 whole tags. UDT traversal is bounded to 8192 nodes/16 levels; each value is bounded to 10000 nodes/32 levels. Preview replies are bounded to 3 MiB and output projects to 64 MiB.

For each changed destination tag, the operation replaces its `Data` children with the source's validated **Decorated** or **String** representation. Competing raw/L5K/other `Data` representations are removed from that selected tag so old values cannot compete with the merged representation. Definitions and `ForceData` are retained. This normalization can itself make a row `ready` even when its decoded value is equal. No byte-level native memory layout or semantic/native validity is inferred.

## REST and MCP

`POST /api/value-merge/capture` takes `{ "controllerTargetId": "CONFIGURED_TARGET" }` and returns a queued SDK job. Poll `/api/sdk/jobs/{id}`; the successful `resultProject` can be used below. This is the only merge operation that connects, and it requires separate target authorization/configuration.

`POST /api/value-merge/preview`:

```json
{
  "sourceProjectId": "SOURCE_ID",
  "sourceSha256": "EXACT_SOURCE_SHA256",
  "destinationProjectId": "DESTINATION_ID",
  "destinationSha256": "EXACT_DESTINATION_SHA256",
  "tags": [
    { "sourceTag": "Recipe", "sourceProgram": "OriginalProgram", "destinationTag": "Recipe", "destinationProgram": "ReplacementProgram" }
  ]
}
```

Alternatively omit `tags` and specify `sourceProgram`/`destinationProgram` to discover exact same-name pairs in those scopes; null denotes controller scope. Empty/oversized scopes must be narrowed with explicit mappings. Preview writes no project, draft or artifact. Each row reports `ready`, `unchanged` or an error code/message. Errors block the entire apply; the service never silently skips a selected incompatible tag.

`POST /api/value-merge/apply` takes `{ "preview": { ...exact preview request... }, "expectedReviewSha256": "REVIEW_SHA256" }`. Apply recomputes the full preview under the project gate, validates its review and both project hashes, and atomically selects one new immutable offline destination revision. Audit events retain the review hash and source identity/hash. The response is the updated `ProjectInfo`; use the normal export/save-artifact endpoints for a separate file.

The `logix` extension exposes `studio_value_merge_capture` (`control` effect), `studio_value_merge_preview` (`read`) and `studio_value_merge_apply` (`apply`) through the existing fixed MCP dispatch tools. Each receives `{ "request": { ...REST body... } }`.

Generated unit and REST/MCP tests cover nested values, declaration failures, stale hashes, preservation, offline export and rejected capture without controller access. They do not establish live upload or native import/runtime acceptance.

For a focused native offline check, run `scripts/Verify-ValueMergeNative.ps1` with the licensed installed SDK and Logix v38 support. It merges generated scalar/STRING/nested UDT/UDT-array values, exports a new ACD, reopens/builds it in a fresh offline SDK job and compares native re-exported values. Controller operations are disabled with no configured targets. This establishes only that fixture's offline round trip; retain the local evidence and validate intended projects separately.
