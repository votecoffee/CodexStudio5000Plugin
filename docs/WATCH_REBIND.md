# Rebind an existing watch source

Rebind moves a stopped list to a chosen managed Logix snapshot and optional exact program mappings. It preserves the list ID, keys, name, interval, history limit, mode, backend, configured target and recording settings. Apply creates a new immutable L5X source artifact and advances the watch definition revision. Retained recordings and their old definitions remain available by revision; cached latest values, extracted types and the current in-memory history are cleared. Polling remains stopped.

This is a saved-metadata operation. It never opens an SDK project, native reader or controller connection, changes controller configuration, or validates running logic. To create an independent list instead, use [portable template import](WATCH_TEMPLATES.md).

## Desktop

In **Watch lists…**, stop polling, select the saved list and choose **Rebind source…**. Unsaved form edits are not used. Choose a registered destination snapshot, review exact program mappings and select **Preview rebind**. Blank mapping destinations denote controller scope and remain subject to the configured policy. Keys, tag names and member/array selectors are preserved.

The comparison grid shows before/after selected types and dimensions, destination scope and declaration-change flags. The detail panel shows approved/observed old hashes, baseline status, destination hash and review hash. **Preflight details…** shows destination selection and configuration diagnostics. Input changes invalidate the preview. If another client updates the saved list, close this window, refresh the list and preview its new revision.

Choose **Apply stopped-list rebind** after reviewing the changes. The same list advances to the next revision. In **Trends & recording…**, select an older revision to view its retained samples and definitions. The current revision starts without a successful sample; start polling separately when authorized and configured.

## Saved declaration comparison

Before metadata is used only when a readable L5X source matches the watch definition's approved SHA-256. Baseline statuses include:

| Status | Meaning |
|---|---|
| `matching_l5x` | Exact approved bytes were read and parsed. Individual declarations can still be incomplete or unavailable. |
| `source_changed` | Observed bytes differ from the approved hash; those bytes are not used as the old schema. |
| `source_unavailable` | The old source could not be accessed/verified under current file rules. |
| `metadata_deferred` | Matching ACD or oversized L5X was hashed without native conversion/XML inspection. |
| `invalid_metadata` | Matching source could not provide usable L5X declarations. |

A changed or unavailable old source does not by itself prevent rebinding to valid destination declarations. It produces `before_unavailable` rather than an invented comparison. Its observed hash/status is included in the review hash; changes after preview require another preview.

Fingerprints cover selected type/dimensions, base-tag dimensions/external access and the reachable exported base-tag UDT declarations, including member order and hidden/bit metadata. They exclude descriptions and stored values. Declaration ordering outside that reachable graph does not affect the fingerprint. A base UDT change can be reported even when the selected scalar's type is unchanged. Use [project comparison](PROJECT_REVIEW.md) for a detailed component diff.

Flags include `scope_changed`, `selected_type_changed`, `selected_dimensions_changed`, `base_declaration_changed`, `before_unavailable`, `after_unavailable`, `declarations_partial` and `unchanged`. Unknown built-in/module templates and recursive declarations remain partial; matching names alone do not establish equal native layouts. Comparison is bounded to 8192 visited nodes and 16 levels per selection; unavailable comparisons are reported explicitly. SDK stored-value decoding, native template layouts and live types are not verified.

Destination [preflight](WATCH_PREFLIGHT.md) must support every mapped selection and satisfy source/hash, configured target policy, CIP route and combined-budget checks. Missing reader software or disabled controller access can prevent polling while still allowing a valid stopped rebind. Existing mode/target/backend settings are retained; rebind cannot retarget a controller or enable access.

## REST and MCP

`POST /api/watches/{id}/rebind-preview`:

```json
{
  "expectedRevision": 0,
  "projectId": "REGISTERED_DESTINATION_ID",
  "expectedSha256": "EXACT_64_CHARACTER_MANAGED_PROJECT_SHA256",
  "programMappings": [{ "sourceProgram": "OldProgram", "destinationProgram": "WatchProgram" }]
}
```

Omit `programMappings` to retain exact scope names. Mapping rules are the same as [template binding](WATCH_TEMPLATES.md). The response contains `watchId`, `fromRevision`, old expected/observed hashes, `baselineStatus`, `proposed`, `changes`, `preflight`, `canApply`, `blockers` and `reviewSha256`. Preview creates no list/artifact/recording and performs no native execution.

`POST /api/watches/{id}/rebind` body is `{ "preview": { ...the exact preview request... }, "expectedReviewSha256": "REVIEW_SHA256" }`. Apply repeats the preview, checks the review hash, then guards the current destination and stopped watch revision while committing. A running list, changed revision/source/mapping/review or invalid destination is rejected. Repeating an applied request fails its expected watch revision. The returned status is the same watch ID at revision +1 with no current sample.

The `watches` extension exposes `studio_watch_rebind_preview` with `read` effect and `studio_watch_rebind_apply` with `apply` effect. Both take `{ "watchId": "...", "request": { ...REST body... } }` through the stable MCP bridge. No fixed MCP tool was added and neither operation authorizes starting polling.

Apply records a `source_rebound` event under the new watch revision, including old/new source hashes and the review hash. This cutover event is forced even when recording is paused; the pause setting stays unchanged. Old samples/definitions remain under their original revision and normal retention rules. Existing source files and prior source artifacts are preserved.

Generated tests cover declaration changes, partial/missing/changed baselines, exact scope policies, revision/hash rejection, active-list rejection, retained samples/settings across restart and paused-recording cutovers. REST/MCP and off-screen GUI checks cover the shared workflow. These checks do not establish native or controller behavior.
