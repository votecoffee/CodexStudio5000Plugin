# Comparison reports and history

Export a pinned Logix comparison as a self-contained HTML or JSON report through the shared service. Reports include **every matching change**, independent of the current comparison page. The same comparison engine, normalization rules and rung-occurrence alignment power the GUI, REST and MCP operations.

## Report contents

Each report records its schema/application version, creation time, both managed snapshot IDs/names/revisions/SHA-256 hashes, filter, rung-number option, total change counts and matching differences. Per-component hashes and exact source selectors remain available. Source file paths, controller routes and credentials are omitted. Display/component names and requested source content can still be sensitive; review the file before sharing it.

- **XML snippets** are optional and omitted by default. Requested normalized snippets retain the comparison engine's explicit 4096-character truncation flags.
- **Ladder drawings** are optional passive SVG generated from changed `Rung` entries. Left/right absence is retained. Invalid or unsupported rung text produces rendering diagnostics instead of an invented drawing. Whole added/removed routines are listed as components and are not expanded into rung drawings.
- HTML contains embedded styling, encoded source labels/snippets, generated passive SVG and a restrictive content-security policy. It has no scripts, external assets or network dependencies. JSON retains the same structured content.

Limits are 5000 matching changes, 200 changed-rung entries when drawings are requested and 16 MiB per exported file. Limit failures publish no report/history entry. Narrow the filter or omit XML/drawings rather than accepting a silently truncated report. Existing project/node/depth and rung-alignment budgets still apply. The comparison request's offset/limit do not restrict export coverage.

These are structural saved-XML review artifacts. They do not establish compiler binding, native semantic equivalence, live power flow, simulation, runtime behavior or native Studio acceptance.

## Desktop

In **Compare projects…**, compare the selected snapshots and filters first. The bottom **Review report** row lets you select HTML/JSON, XML snippets and ladder drawings, then **Export report…** to a new file inside configured `OutputRoots`. The GUI initially selects HTML with drawings. Changed snapshot selections or filters require a fresh comparison before export. The service rechecks both hashes before creating a report; export preserves both projects and their originals.

**Report history…** opens a searchable, paged list of retained reports. Select one to inspect full snapshot/report metadata:

- **Save report copy…** verifies the retained artifact hash and saves a new file through the guarded artifact service. Existing files are preserved.
- **Review changes / notes…** opens the report's recorded changes with review states, notes, search and progress counts. See [annotations](#annotations).
- **Compare recorded snapshots** returns to the comparison browser only if both managed snapshots still match the report's recorded hashes. Changed/missing snapshots leave the retained report available for export; select current snapshots explicitly for a new comparison.
- **Forget history entry** removes only that history entry at its expected report hash. Its immutable artifact, previously exported copies and all projects remain preserved. It does not reclaim artifact storage.

History survives service restarts and project changes. Up to 200 entries are retained locally under the protected state directory's `comparison-reviews/`; forget an entry before exporting another report when the history is full. Exported bytes and their metadata live in the managed artifact directory. This is a local report library, not an approval or deployment record. Keep it and generated reports out of the public repository.

## Annotations

Track each recorded change as **Unreviewed**, **Reviewed** or **Needs attention**, with an optional note up to 2000 characters. Summary counts cover the entire report; paging and filters show a subset. Needs-attention changes remain open alongside unreviewed changes. Review states are local tracking metadata and do not authorize project/controller actions or establish native acceptance.

New exports retain a private, hash-verified change catalog containing metadata/selectors/component hashes, without XML snippets or drawings. Each change ID includes the exact report SHA-256, recorded ordinal and component identity. API/export ordinals start at zero; the GUI/HTML display adds one. Two reports of the same snapshots have separate review state. Notes survive service restarts and later project edits; they are never rebound to a newer report or recomputed from current projects.

The editor retains drafts across pages and filters, marking pending rows with `*`. **Save pending notes** sends up to 100 changed rows atomically with the report hash and the revision on which editing started. Refreshing while drafts exist does not advance that edit revision. A conflicting save retains drafts; **Discard pending / reload** clears them and reads current saved notes. Save or discard pending edits before closing or exporting. Setting a change back to Unreviewed with an empty note removes its stored annotation.

**Export notes JSON…** creates a separate, hash-verified artifact at the exact loaded report/review revision. It includes report/snapshot identity, global progress and only explicitly annotated changes with their ordinals, identities, states and notes. Unlisted changes remain unreviewed. It preserves the original HTML/JSON report bytes. Save the notes alongside that exact report when sharing a review. Display/component names and free-form notes may contain private information; keep them out of the public repository.

Saved annotations are local under `comparison-annotations/`. Each request supports 1..100 distinct changes; total stored annotations are bounded to 2 MiB. Page size is 1..100, note-search length 256 characters, page responses 4 MiB and notes exports 16 MiB. Invalid IDs, cross-report changes, duplicate updates, unsupported states, oversized notes and stale hashes/revisions reject the complete save. Original report artifacts and change catalogs are verified before reads, saves and exports. Audit events record generic metadata and a state hash, without duplicating note text.

Reports created before catalogs were available remain readable/exportable. Older JSON reports can derive identities from their verified recorded change array without creating local state on read. Older HTML reports without a catalog require a new report export before annotations are available; current projects are never used to reconstruct an old report's changes. Forgetting a history entry preserves its artifact/catalog and local annotation file but removes access through the report library, so export notes first if they need to remain available there as files.

If saving a chosen output file fails after report creation, the created report remains available in history for a later **Save report copy…**. Export never overwrites a previous report.

## REST and MCP

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/comparison-reviews/export` | Create immutable report artifact/history entry |
| GET | `/api/comparison-reviews?query=&offset=0&limit=50` | Search names, snapshot hashes, recorded filters or report IDs; limit 1..100 |
| GET | `/api/comparison-reviews/{id}` | Read metadata and verify the retained artifact |
| POST | `/api/comparison-reviews/{id}/forget` | Forget the entry with `{expectedSha256}`; preserve the artifact |
| GET | `/api/comparison-reviews/{id}/annotations?query=&status=all&offset=0&limit=50` | Recorded change identities, notes, global counts and loaded annotation revision |
| POST | `/api/comparison-reviews/{id}/annotations` | Save an atomic `{expectedReportSha256, expectedRevision, changes}` batch |
| POST | `/api/comparison-reviews/{id}/annotations/export` | Export notes/progress with `{expectedReportSha256, expectedRevision}` |
| GET | `/api/artifacts/{id}` | Download verified bytes |
| POST | `/api/artifacts/{id}/save` | Save `{path}` inside `OutputRoots`, without overwriting |

Export request:

```json
{
  "comparison": {
    "leftProjectId": "<managed snapshot ID>",
    "leftSha256": "<current snapshot SHA-256>",
    "rightProjectId": "<managed snapshot ID>",
    "rightSha256": "<current snapshot SHA-256>",
    "query": "Rung",
    "includeRungNumbers": false
  },
  "format": "html",
  "includeXml": false,
  "includeLadder": true
}
```

The response contains `artifact` and `review` metadata. Export defaults to HTML with XML/drawings omitted at the API; GUI options are explicit. Export always requires current project hashes. Reading/downloading a retained report does not require current project hashes and never recomputes the comparison.

The `logix` extension exposes `studio_comparison_review_export`, `studio_comparison_reviews`, `studio_comparison_review_get` and `studio_comparison_review_forget`. Discover schemas through the fixed MCP bridge. Export/forget use the apply channel because they change local metadata/artifacts; list/get use read. Save the returned artifact with `studio_save_artifact`. None connects to a controller or changes a PLC program.

Annotations use `studio_comparison_annotations` on read and `studio_comparison_annotations_update` / `studio_comparison_annotations_export` on apply. Updates contain `changes` entries with `changeId`, `status` (`unreviewed`, `reviewed`, `needs-attention`) and `note`. Initial annotation revision is 0; the first successful save creates revision 1. Progress/export always refers to the exact report hash, including after its project snapshots change.

Version 0.22 requires the updated shared service, desktop and extension package. Install when the host is idle; worker reload alone cannot add service endpoints. The fixed MCP bridge remains compatible.
