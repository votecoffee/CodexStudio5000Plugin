# Saved comparison sessions and merge mappings

All clients use the shared service. Definitions are stored locally under the protected state directory in `comparison-sessions/` and `merge-mappings/`; none belong in the public repository. Each kind supports up to 200 named definitions, with revision checks for updates and deletion. Definition IDs are generated independently of display names.

## Comparison sessions

A session saves two managed project IDs and their exact hashes, comparison filter/page, rung-number setting and an optional ladder bookmark: routine selectors, aligned rung offset, changed-rung filter and routine query. Saving validates the snapshots and bookmark. Reading reports `canResume=false` and issues if either snapshot changed or became unavailable. It retains the original hashes rather than silently selecting newer revisions.

Resume through the existing read-only comparison and ladder operations. To continue against changed projects, explicitly compare current snapshots and update the session with its expected revision. Saved sessions do not recreate missing projects, reconnect to controllers or restore a previously reviewed mutation.

## Reusable merge mappings

Mapping templates contain only `schemaVersion: 1`, a display `name` and 1..256 `tags` with `sourceTag`/`destinationTag` pairs. They omit project IDs, paths, program names, controller routes, values and previous review hashes. Unknown fields and duplicate JSON properties are rejected. Destination names must be unique, ignoring case. Whole base-tag names are supported; member/array-cell selections are outside this merge format.

```json
{
  "schemaVersion": 1,
  "name": "Reusable enable mapping",
  "tags": [{ "sourceTag": "Enabled", "destinationTag": "OtherEnabled" }]
}
```

Bind a saved mapping's exact revision to source/destination project IDs, current hashes and explicit source/destination program scopes (`null` means controller scope). Binding produces a fresh `proposed` merge request and value `preview` through the existing merge engine. Missing tags, incompatible UDT/array layouts and unsupported values remain blocking diagnostics. Binding neither changes a project nor starts a capture.

Apply remains a separate call to `/api/value-merge/apply` with the returned proposed request and review hash. That call rechecks the snapshots and exact reviewed values. Changing a stored mapping does not modify a previously returned explicit request. Original projects are preserved; export a separate working copy for native validation.

## Desktop

In **Compare projects…**, the saved-session row offers refresh, load, save new, update and delete. Load restores the pinned comparison and its exact ladder row. A stale session leaves the comparison empty with diagnostics; explicitly compare current snapshots before updating it.

In **Merge tag values…**, select the source/destination snapshots and scopes, then choose **Reusable mappings…**. Edit source/destination whole-tag names in the grid, save a new mapping or update a loaded one, then choose **Use saved mapping**. The merge window displays the freshly bound value review. It requires a separate click on **Apply reviewed offline merge** to change the destination. Unsaved mapping edits must be saved before use. Both editors retain the revision they loaded when updating; refreshing the library does not authorize overwriting another client's changes.

## REST and MCP

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/comparison-sessions` | Bounded summaries |
| GET | `/api/comparison-sessions/{id}` | Definition and resume diagnostics |
| POST | `/api/comparison-sessions` | Save new, or update with `id`/`expectedRevision` |
| POST | `/api/comparison-sessions/{id}/delete` | Delete with `expectedRevision` |
| GET | `/api/merge-mappings` | Bounded summaries |
| GET | `/api/merge-mappings/{id}` | Exact saved template/revision |
| POST | `/api/merge-mappings` | Save `{template}`, or update with `id`/`expectedRevision` |
| POST | `/api/merge-mappings/bind` | Fresh read-only value preview for an explicit binding |
| POST | `/api/merge-mappings/{id}/delete` | Delete with `expectedRevision` |

The `logix` extension exposes nine `studio_comparison_session*`/`studio_merge_mapping*` operations, including plural list names. Discover them through the existing fixed MCP bridge. Listing, reading and binding use the read channel; saving/deleting definitions use apply. None are controller operations. Project mutations retain their existing separate review/apply boundary.

Version 0.19 needs the updated shared service and desktop as well as its extension package. Install when the workspace is idle. Reloading a worker alone cannot add missing service endpoints.
