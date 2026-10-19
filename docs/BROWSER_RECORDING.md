# Tag browser, trends and recording

Version 0.8 provides a shared snapshot browser and durable watch recordings. REST, the desktop and the reloadable `watches` extension use the same service state. Browsing, watch creation, recording configuration, history queries and CSV export never initiate controller access. Polling and controller-upload operations keep their existing explicit authorization, source hash, target scope and controller gates.

Version 0.13 adds a reader choice to browser watch creation and watch settings. `Sdk` remains the default. **Plan CIP reads (offline)** shows bounded symbolic reads for selected saved tags/UDT members without connecting. `LibPlcTag` watches require Online mode, an L5X source and a configured CIP route; see [CIP reader setup and limitations](CIP_READS.md).

Version 0.14 adds **Preflight selection…** in the browser and **Preflight draft…** in watch settings. These inspect all rows, source hashes, target/controller/reader configuration and combined CIP budgets without creating an artifact, saving a list or opening a reader. See [watch preflight](WATCH_PREFLIGHT.md).

Version 0.15 adds [portable watch templates](WATCH_TEMPLATES.md) with saved-definition export, explicit program mapping, preflight/review hashes and new stopped-list import. Import preserves the template's interval/history limit and uses the browser's shared immutable source-artifact path.

Version 0.16 adds [source rebinding](WATCH_REBIND.md) for existing stopped lists. Applying a reviewed snapshot/program mapping preserves the list ID, settings and retained recording revisions, clears current samples and records a `source_rebound` cutover. Select the older revision in the trend window to inspect pre-rebind data.

## Desktop workflow

1. Open **Tag / UDT browser…** from the main workspace or watch window. Choose a registered Logix project, open an ACD/L5X, or enter a completed SDK upload job ID. ACD conversion needs the installed native bridge; L5X browsing does not. Loading an upload result reads its saved metadata; it does not perform another upload.
2. Search by tag, program, data type or description. Expand tags to browse declared UDT members and array cells. Each branch loads up to 100 nodes; double-click **Load more…** for the next page. Multidimensional arrays use paths such as `[1][2].Count`. UDT definitions are also listed separately. Built-in types without exported member declarations have no invented layout.
3. Check exact base tags or members and choose **Create watch list**. A selected watchable node can also be used without checking it. The service validates the schema path, preserves a source artifact and creates a stopped list. Offline is the default. Online requires a selected configured target and still does not start until **Start polling**.
4. In **Watch lists…**, select the list and choose **Trends & recording…**. Numeric leaves of structures/arrays and BOOL values appear as selectable series. BOOL traces use 0/1 steps. All selected series share one value axis; choose a smaller subset when their scales differ. Hover a point for its exact recorded JSON value. Strings and complex objects remain available in recorded samples/CSV.
5. Follow the most recent window or uncheck **Follow last** and set local start/end times. Choose the watch revision to inspect old configurations separately. The viewer refreshes every two seconds while following. At most 5,000 entries / 8 MiB are displayed per query; narrow the range if the display-limit message appears. CSV export includes the entire selected retained range, independent of that display limit.
6. Use **Pause recording** / **Resume recording** without stopping polling. Retention defaults to seven days / 64 MiB per list and can be configured to 1–365 days / 8–256 MiB. Closing the GUI leaves service polling and recording running. A service restart restores recordings and cached history, but polling always remains stopped.

## Browser API

`POST /api/tags/browse` accepts `TagBrowseRequest`:

```json
{ "projectId": "REGISTERED_LOGIX_ID", "query": "Station", "offset": 0, "limit": 100 }
```

The result includes source SHA-256, controller name, program names, exported UDT definitions and a page of nodes with exact tag/program/member identities, dimensions, expandability and watchability. A root query returns tags across controller/program scopes; `programName` filters an exact program. Search is case-insensitive; identity selection uses the exact exported names. Pagination limit is 1–256 and the total is independent of the page.

To expand a tag/member, supply `projectId`, returned `expectedSha256`, `tagName`, optional `programName` and optional `memberPath`. To browse a type declaration, supply `dataTypeName` separately from a tag selector. Children come from declarations, without decoding stored array values. Alias tags and hidden implementation members are not watchable through the browser. Visible declared BOOL/bit members remain selectable: the SDK decodes their values, with no inferred offsets. Changing project revisions invalidates hash-bound expansion or watch creation.

`POST /api/tags/watch` accepts `BrowserWatchRequest`:

```json
{
  "projectId": "REGISTERED_LOGIX_ID",
  "expectedSha256": "RETURNED_PROJECT_HASH",
  "name": "Station trend",
  "mode": "Offline",
  "intervalMs": 1000,
  "tags": [{ "key": "position", "tagName": "Station", "programName": "MainProgram", "memberPath": "Axis.Position" }]
}
```

The resulting stopped watch uses an exported immutable L5X artifact and its exact hash. Existing watch/tag limits and target policies apply. Member selection still reads the whole underlying base tag during native polling; array paging does not change SDK transfer limits.

## Recording API and extension catalog

Discover the current `watches` generation with `studio_extensions_catalog`, describe the desired operation, then invoke using `studio_extensions_read` or `studio_extensions_apply` with that generation. The fixed MCP tool count remains 56.

| Catalog operation | Effect | REST endpoint |
|---|---|---|
| `studio_tag_browse` | read | POST `/api/tags/browse` |
| `studio_tag_create_watch` | apply | POST `/api/tags/watch` |
| `studio_watch_recording` | read | GET `/api/watches/{id}/recording` |
| `studio_watch_recording_configure` | apply | POST `/api/watches/{id}/recording` |
| `studio_watch_recording_query` | read | POST `/api/watches/{id}/recording/query` |
| `studio_watch_recording_export` | apply | POST `/api/watches/{id}/recording/export` |

Configure with `{ "settings": { "enabled": true, "retentionDays": 7, "maxMiB": 64 }, "expectedRevision": 0 }`. Recording settings have their own revision, separate from the watch-definition revision. A stale settings revision is rejected.

Query/export accept `watchRevision` (default current), `from`/`to` ISO-8601 timestamps, `afterIndex` (default 0) and `limit` (1–5000, default 1000). Queries are ordered by the durable index and include `nextIndex`, `hasMore`, server time, current stale/state, recording configuration and the selected revision's definition. Continue with `afterIndex: nextIndex`; retained records can start after older expired indices. Time bounds are inclusive. CSV export honors the revision/range/cursor, but exports all matching retained records regardless of `limit`.

Export returns a normal hash-checked CSV artifact. Download with GET `/api/artifacts/{id}` or save a new file inside configured OutputRoots with POST `/api/artifacts/{id}/save`. The desktop may save a verified download to a user-selected new local filename. Existing files are preserved. CSV columns contain durable index, watch revision, session, event kind, start/completion UTC timestamps, mode, key, program, tag, selected member, declared base data type, quality, exact JSON value and error/detail. Text cells starting with spreadsheet formula prefixes are neutralized.

## Persistence and gaps

The recorder writes compact UTF-8 JSON lines under ignored `state/watches/{id}/recording`, rotating segments at approximately 4 MiB. Each successful append flushes the file to disk. A monotonic cursor, recording settings and revision definitions are persisted separately. An incomplete final line after interruption is removed before another append, with a recovery note. Complete malformed records fail validation; preserve those files for investigation rather than silently deleting them.

Retention removes oldest whole segments when their last-write time expires or total journal bytes exceed the selected cap. A segment can contain several minutes of samples, so retention is segment-granular and can retain less than the requested range. Cleanup runs on startup, writes, queries and status/configuration access. The byte cap covers journal segments; revision definitions, source/decoder runs, exported artifacts and deleted-list evidence remain for inspection. There is no background cleanup of those other files. Recordings begin after this upgrade; pre-upgrade in-memory history cannot be reconstructed.

Records include `sample`, `start`, `stop`, `fault`, `service_restart`, `recording_disabled` and `recording_enabled` events. A changed watch definition starts a new revision without erasing earlier recordings; queries default to the current revision so different tag selections are not combined. The old bounded `/history` endpoint remains available and restores its current-revision sample cache from retained recordings. Recording may be paused while latest values continue to update.

The trend projection breaks lines at events/session changes, bad/missing values, nonmonotonic times and inter-sample delays greater than `max(2 × interval, 1 second)`. It shades communication/session interruptions and the stale tail; it does not interpolate across gaps or substitute an old value as a new sample. At most 128 numeric/BOOL leaves are plotted, with bounded structure traversal. Graph coordinates use floating point; raw recordings/CSV preserve exact JSON integers, including values beyond double precision. Sequential multi-tag reads are not atomic snapshots. Offline samples always represent saved data. Native live polling, upload behavior and controller-specific timing still require validation on the intended controller.

## Update

This release adds shared-service primitives and desktop/worker code. Publish/restart those binaries only when SDK jobs, watch readers and extension calls are idle, then activate installed v0.8.0 packages. Configuration and controller gates are preserved. The unchanged MCP dispatch tools can continue with the same address/token; future compatible extension features still reload independently. See [EXTENSIONS.md](EXTENSIONS.md).
