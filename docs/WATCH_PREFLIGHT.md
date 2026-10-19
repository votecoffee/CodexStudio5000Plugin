# Watch preflight

Preflight inspects a draft, a saved watch definition or tag-browser selections before polling. It reports the observed source hash, individual declaration/support problems, configured target policy, controller gate, reader presence and CIP read budgets. REST, MCP and the desktop use the same Core operation. No reader, SDK project, native conversion, job or controller connection is opened. Preflight creates no watch, source artifact, recording or approval.

## Desktop

In **Watch lists…**, edit the fields and choose **Preflight draft…**. This checks the current form, including unsaved changes. In **Tag / UDT browser…**, check tags/members and choose **Preflight selection…**. Browser inspection uses the exact managed revision and hash without exporting another artifact.

The viewer shows every selection with its declared type, remaining dimensions, scalar read count where available, and diagnostics. Select a row for details. **Source and configuration** shows independent failures such as an unknown target, disabled controller access or missing reader. A successful declaration row can appear alongside configuration failures; inspect the overall result.

Preflight does not save, start, remove unsupported entries, switch backends or update a source hash. After correcting the draft, run it again and save normally. A saved list whose source changed must be reviewed and explicitly updated through the ordinary stopped-list/revision workflow.

## REST and MCP

`POST /api/watches/preflight` accepts exactly one of:

```json
{ "settings": { "name": "Draft", "tags": [{ "key": "count", "tagName": "Record", "programName": "WatchProgram", "memberPath": "Inner.Count" }], "expectedSha256": "EXACT_64_CHARACTER_SOURCE_SHA256", "sourcePath": "C:\\Projects\\Fixture.L5X", "mode": "Offline", "backend": "Sdk" } }
```

```json
{ "watchId": "SAVED_WATCH_ID" }
```

```json
{ "browser": { "projectId": "REGISTERED_LOGIX_ID", "expectedSha256": "EXACT_64_CHARACTER_PROJECT_SHA256", "name": "Browser draft", "tags": [{ "key": "count", "tagName": "Record", "programName": "WatchProgram", "memberPath": "Inner.Count" }], "mode": "Offline", "backend": "Sdk" } }
```

Saved lists also support `GET /api/watches/{id}/preflight`. The report includes `watchId` and `watchRevision` for the definition inspected. The `watches` extension exposes all three forms as the read operation `studio_watch_preflight`, called through `studio_extensions_read` with `{ "request": { ...one form above... } }`. No fixed MCP tool was added.

Drafts use the normal watch settings shape and source/artifact access rules. List-level constraints such as distinct keys, supported mode/backend, interval and source/hash syntax are request errors. Tag selector syntax, declaration absence, aliases, hidden members and applicable policy failures are reported per selection. Browser preflight rejects stale managed hashes. Draft/saved source changes instead produce a blocked `source_hash` check and return the actual observed SHA-256; rows describe those observed bytes. Source path authorization failures remain request errors.

## Interpreting results

Overall `status` is:

| Status | Meaning |
|---|---|
| `blocked` | At least one checked configuration, source, selection or combined-budget constraint failed. |
| `incomplete` | No checked constraint failed, but metadata inspection is deferred. |
| `local_checks_passed` | All implemented local checks passed. Native/live behavior and permission to start remain separate. |

Selection statuses are `supported`, `blocked` or `deferred`. SDK whole-tag declarations, strings and declared UDT/array paths can be checked without native execution; missing built-in member templates remain deferred. SDK transfer size, stored value decoding and online access are not proven by a declaration check. ACD sources and L5X files larger than 64 MiB are hashed without native conversion or XML inspection. The CIP backend still requires L5X; ACD is blocked for that backend. Malformed declarations produce metadata diagnostics rather than native fallback.

For `LibPlcTag`, the budget reports fully supported selections only:

- `uniqueSupportedLeafReads` deduplicates symbolic scalar addresses across those selections.
- `supportedPlanNodes` includes each selected extraction tree, so duplicate structures still consume node capacity.
- `scalarValueBytes` sums unique scalar storage widths; it excludes protocol, metadata and JSON overhead.
- `nominalReadsPerSecond` divides the unique scalar count by the requested interval. It is a workload calculation, not a measured throughput estimate.
- `complete` is false if any selection is blocked/deferred. `fitsListLimits` evaluates the counted subset against 256 unique reads and 2048 nodes. Both fields matter: a partial budget that fits does not validate the entire list.

Each selection still obeys the [CIP planner's type, array and depth limits](CIP_READS.md). Entire selections that fail planning are excluded from the budget. No failing selection is silently converted into a smaller watch. Shared bounds are checked across the supported set, including lists whose selections pass individually but exceed a combined limit.

Reader configuration checks test executable existence only; they do not launch it or certify dependencies, licensing or loadability. Preflight does not reserve capacity or authenticate live declarations. Save/start repeat their own validation and controller gates. A preflight response cannot authorize a controller action or bypass those checks. Unit, REST/MCP and generated desktop validation cover this local workflow; live reader acceptance remains separate.
