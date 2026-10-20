# Legacy review copy

Use this workflow for an explicitly requested **REVIEW ONLY v35-to-v32 copy**. Normal `studio_version_compatibility_*` conversion remains upgrade-only and returns `downgrade_unsupported` for lower revisions. This workflow does not turn a header edit into a deployable downgrade.

## Native source and exact preparation

Discover `studio_legacy_review_preview` and `studio_legacy_review_prepare` through the live extension catalog. The preview uses the read channel; preparation uses apply. Both run in the shared service and start no SDK job or controller connection.

The initial profile accepts a completed **offline SDK native L5K export job** with its retained native v35 L5X result snapshot. Reuse an existing successful export when available. If a native export is actually needed and authorized, obtain it through the existing SDK job operations using a separate copy and `SaveAsAsync` to L5K. Retain the original intended project, native source/export hashes and job receipts. A caller-supplied file path or a patched version comment is not native-export provenance.

Preview arguments:

```json
{
  "exportJobId": "<completed-offline-native-export-job>",
  "l5kArtifactId": "<that-job-l5k-artifact>",
  "expectedSha256": "<native-l5k-sha256>",
  "purpose": "Engineering review in a separately installed v32 editor",
  "profile": "v35-to-v32-review"
}
```

The parser requires unique native IE_VER/controller/Local declarations, consistent v35 source revisions, strict UTF-8 and bounded syntax. Review every reported span and old/new value. Span offsets are zero-based UTF-16 character positions in decoded text, excluding the BOM; source/output hashes bind the actual UTF-8 bytes. The only supported changes are:

- IE_VER `2.26` to `2.23`.
- Controller and Local module `Major` from `35` to `32`.
- Omission of the controller's `AutoDiagsEnabled` and `WebServerEnabled` fields, when present with supported scalar values.

All unselected bytes remain intact, including ordinary values, code, remote module revisions, BOM, line endings and the original exporter version comment. Unknown revisions, ambiguous declarations, incomplete/indeterminate exports and controller-effect jobs are rejected. No schema-error repair loop removes arbitrary configuration. The profile is a bounded import-input recipe; it does not establish instruction, hardware, motion or safety compatibility.

Call prepare with `{ "preview": <the exact request>, "expectedReviewSha256": "<preview-review-sha>" }`. It rechecks the completed job, source snapshot, artifact metadata/bytes, purpose and field edits. It creates an immutable L5K artifact and `legacy_review_input` JSON manifest marked review-only. Preserve both IDs/hashes. It also returns an `importRequest`; it does not execute it.

## Explicit native acceptance

Only when the user has requested the native review copy, submit the returned `importRequest` through `studio_sdk_start_job`. It opens the prepared L5K on a private copy and explicitly calls `SaveAsAsync` to `ReviewOnly-v32.acd` inside that job's private output directory, matching the native import/save route used for this bounded workflow. It contains no ConvertAsync or controller method. Native v32 installation, licensing and project support are still required. Retain the job ID and poll its outcome; never automatically replay an interrupted or indeterminate job.

If native import fails or reports unsupported settings, keep its diagnostics and report the gap. Do not broaden the omission policy or silently default motion settings. A successful import/save produces separate ACD/L5X artifacts; it is not yet evidence that the saved ACD reopens correctly.

Reopen the exact saved ACD in a **separate explicit SDK job**, using `sourceArtifactId`, its `expectedSha256`, and a single `SaveAsync` call. Reuse an already completed, source-bound readback when available. Verify the retained job chain and actual native software/controller/Local revision fields. The supplied-file validator cannot independently prove native execution/provenance.

## Review readback and retain results

Register or use the managed native readback and the **original intended v35 project** as separate pinned snapshots. Discover the `legacy-review-readback` profile with `studio_validation_profiles`, then call `studio_validation_preview`:

```json
{
  "profile": "legacy-review-readback",
  "candidate": {"projectId": "<native-v32-readback-id>", "sha256": "<native-v32-l5x-sha>"},
  "reference": {"projectId": "<intended-v35-id>", "sha256": "<intended-v35-sha>"}
}
```

The report supports up to 64 MiB / 1,000,000 XML elements per managed input. It separates:

- Actual v35 reference and v32 readback revision fields.
- Application and readable AOI routine inventories/code versus annotations.
- Ordinary tag inventories, definitions, aliases and every stored value representation; readable AOI defaults and UDT/AOI layouts.
- Motion tag configuration, other configuration and direct routine-reference findings.
- Protected AOI envelope metadata/parameter elements versus **all direct text/CDATA payload segments**, including text after child elements. Only payload hashes are exposed; equality does not establish opaque-code semantics.

Strict comparison is the default. Explicit `nativeRules` may normalize MESSAGE formatting, L5K/RLL whitespace outside quoted values, or display rung numbers. This profile also offers `normalizeProtectedPayloadWhitespace`: it requires nonempty valid base64 and compares decoded opaque bytes, without interpreting their semantics. Unknown/nested payload layouts remain unsupported. No data representation is silently dropped.

Native version fields, metadata and motion defaults can produce findings. Inspect their exact paths/hashes and retained native messages. Intentional metadata/configuration changes may use the existing exact `expectedDifferences` policy with a reason for each field. A native protected-AOI re-encoding must be reviewed under its separate `protected-aoi-payload` check with both exact payload hashes and a reason; matching metadata does not approve it. Such an expected byte difference remains visible and never establishes opaque-code equivalence. Legacy review cannot waive target revision, readable code, ordinary values/layouts, AOI defaults or UDT/AOI layouts as metadata exceptions. No wildcard acceptance is available.

Record the exact reviewed pass/fail/unsupported report through `studio_validation_record`; retrieve it by artifact ID/hash with `studio_validation_receipt` and save it with `studio_save_artifact`. A pass covers the named offline checks only. Retain the original source, preparation manifest, native job chain, readback and validation receipt together. Export new filenames visibly labeled **REVIEW ONLY**. Safety semantics, runtime behavior and commissioning/deployment acceptance remain outside this workflow.

## GUI

Open Diagnostics and project workflows → **Legacy review copy**. Paste the completed export job/artifact/hash into the operation editor, review preparation, then use the explicit SDK workflow separately. For readback validation, select the intended v35 snapshot as Project / modified logic and the native v32 snapshot as Candidate / native readback. The GUI calls the same service operations as MCP.
