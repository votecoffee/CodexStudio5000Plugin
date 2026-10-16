# Watch lists and extracted UDT structures

Watch lists are shared service resources. REST, Codex MCP tools and **Watch lists…** in the desktop use the same saved definitions, reader sessions, latest values and history. A GUI/chat client does not need to remain open. Closing the watch window leaves polling running; use **Stop polling** to end it. Lists never start merely because they were saved, viewed or restored after service restart.

## Create and poll

`POST /api/watches` or `studio_watch_save` creates a stopped list. Example online definition:

```json
{
  "name": "Process watch",
  "sourcePath": "C:\\Projects\\CurrentMachine.ACD",
  "expectedSha256": "EXACT_64_CHARACTER_SOURCE_SHA256",
  "controllerTargetId": "authorized-plc",
  "mode": "Online",
  "intervalMs": 1000,
  "historyLimit": 100,
  "tags": [
    { "key": "count", "tagName": "CycleCount", "programName": "MainProgram" },
    { "key": "station", "tagName": "Station", "programName": "MainProgram" },
    { "key": "position", "tagName": "Station", "programName": "MainProgram", "memberPath": "Axis.Position" },
    { "key": "recipe", "tagName": "Recipes", "programName": "MainProgram", "memberPath": "[1].Setpoint" }
  ]
}
```

Get the source hash with `studio_sdk_inspect_source`. Choose one `sourcePath` or `sourceArtifactId`; hashes are checked when saving and again when starting. Use a matching saved project or a fresh uploaded ACD. Use `mode: "Offline"` and a saved ACD/L5X for local validation. `examples/WatchDemo.L5X` contains a nested UDT, an array of UDTs, BOOL and STRING fixtures. An offline sample's mode remains Offline even when its read quality is good.

Tag names identify exact base tags. A null/omitted `programName` selects controller scope only where the configured policy permits it. Member selection happens after the whole base tag has been extracted, so it cannot escape that tag or its program. Supported selectors include `Inner.Count`, `Samples[1]`, `[1].Setpoint` and `[0][1].Count`. Wildcards, XPath fragments, negative indices and aliases are rejected. Select the authorized base of an alias explicitly.

Start with `POST /api/watches/{id}/start` or `studio_watch_start`. Online starts require `EnableControllerOperations`, the configured target, actual monitoring authorization and allowed `GoOnlineAsync`/`GetTagValueAsync` methods. Program policies apply to every base tag. This feature does not broaden an existing target's method/program policy. Controller operations remain disabled by default.

Read `GET /api/watches/{id}` / `studio_watch_get` for state, latest sample, extracted type definitions and errors. A sample contains sequence, start/completion timestamps, mode and values:

```json
{
  "key": "station",
  "tagName": "Station",
  "programName": "MainProgram",
  "dataType": "StationData",
  "value": { "Ready": true, "Axis": { "Position": 12.5 }, "Samples": [10, 20, 30] },
  "quality": "good",
  "error": null
}
```

`quality` is per value. Failed/unavailable reads have null values and diagnostics; old values are not substituted as successful current data. `stale` also becomes true when a list is stopped/faulted/starting or its latest sample is overdue. Always inspect both quality and stale state. Multi-tag reads are sequential, not an atomic controller snapshot. JSON numbers preserve .NET integer values, but JavaScript callers should handle 64-bit integers with an appropriate JSON parser.

Polling is serial within a list. Interval is measured between cycle starts where achievable; a slower cycle completes before the next starts, without accumulating overlapping polls. Sources open once per run. Native SDK operations are serialized with conversion/jobs; time spent waiting or loading can exceed the requested interval. Starting a list may take substantial time while Studio loads the source and decoder. Faults stop the session without automatic reconnect; inspect the error and explicitly restart with a current source.

Stop with `POST /api/watches/{id}/stop` / `studio_watch_stop`. Edit a stopped list with `POST /api/watches/{id}` and `{ "settings": { ...definition... }, "expectedRevision": 0 }`, or `studio_watch_save` with `watchId` and `expectedRevision`. Delete through `POST /api/watches/{id}/delete` / `studio_watch_delete`. Listing and history use `GET /api/watches` / `studio_watch_list` and `GET /api/watches/{id}/history?limit=100` / `studio_watch_history`.

Definitions, the latest sample and v0.8 recordings persist under ignored local state. The small history cache is restored from retained recordings for the current watch revision after restart; the latest value stays stale until an explicit start. Recordings default to seven days / 64 MiB per list and have separate pause/resume/retention controls. Source/decoder files and deleted-list evidence remain local; recording retention does not clean these files. See [tag browsing, trends and recording](BROWSER_RECORDING.md).

## Pull live UDTs and expose their structures

`POST /api/structures/pull` or `studio_structure_pull` with `{ "controllerTargetId": "authorized-plc" }` starts the existing SDK upload-to-new-project workflow. It requires enabled controller access and `UploadToNewProjectAsync` in the target policy. It does not download, change controller mode or write a controller tag. Poll the returned SDK job until successful.

Pass its `jobId` to `POST /api/structures/extract` or `studio_structure_extract`:

```json
{ "jobId": "SUCCESSFUL_UPLOAD_JOB_ID", "dataTypeName": "StationData", "includeValues": true }
```

Alternatively choose a managed `projectId`. Optional `tagName`, `programName` and `dataTypeName` filter the extraction. Tag values are paged with `offset` and `limit` (default 100, maximum 256); `totalTags` reports the matching count. All matching type definitions are returned independently of the tag page, so large controller uploads still expose their UDT catalog. `includeValues: false` returns declarations without decoding stored values. Types include named members, data types, dimensions, hidden/bit-host metadata and external access. Values are nested JSON objects/arrays. Missing decorated data is reported unavailable rather than invented.

`origin: "controller_upload_snapshot"` and `uploadedAt` distinguish an unmodified successful upload result from `origin: "saved_project"`. `uploadedAt` is the result project's publication time, not a per-tag scan timestamp. Jobs that import/change values after uploading do not get the upload-snapshot label. Uploaded values are a point-in-time snapshot, not interval reads. To continue monitoring, create an Online watch from that job's ACD artifact and hash. After controller definitions change, refresh the upload/source and restart the watch against that schema; saved declarations are not an automatic live schema subscription.

The GUI offers **Extract selected project**, **Pull controller UDTs**, an upload job field and **Extract upload result**, alongside the latest-value tree and history.

## Native decoding and limits

The reader obtains the complete tag with SDK `GetTagValueAsync(..., BYTE_ARRAY)`. It applies those bytes with mode **Offline** to a separate private scratch project, then asks the SDK for that tag's decorated XML. The service parses that native representation into JSON. The connected source is only read; no online tag write, program import, download, mode change or guessed memory-offset decoder is involved. Primitive tags, nested structures and arrays use the same path. STRING literals and numeric radix values are decoded explicitly; unsupported representations carry errors.

Limits: 100 saved lists, four active lists, 1–64 entries per list, 100–86400000 ms interval, history limit 1–1000 samples capped at 8 MiB, 4 MiB per sample, and 10000 nodes/64 levels per extracted value. The SDK's operation limit is 30 kB; oversized whole tags must be exposed as smaller base tags or handled with a separate supported access path. A member selector does not reduce the underlying whole-tag transfer. The [Rockwell SDK Getting Results Guide](https://literature.rockwellautomation.com/idc/groups/literature/documents/gr/ldsdk-gr001_-en-p.pdf) documents SDK prerequisites and operation-size limits; this repository binds the locally installed SDK 2.1.974.

The shared service and native offline fixtures were tested without connecting to a controller. Live polling and fresh upload/extraction for this new feature still require validation on the intended controller/version. Earlier online-edit validation does not establish that boundary.
