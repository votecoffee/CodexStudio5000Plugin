# Portable watch templates

Templates reuse exact tag/member selections and polling settings with another saved Logix project. The shared service exports portable JSON, previews an explicit destination/program binding and creates a new stopped list after the reviewed binding is rechecked. GUI, REST and MCP use the same operations. These operations never open a reader or connect to a controller.

## Desktop workflow

In **Watch lists…**, select a saved list and choose **Export saved template…**. Export uses the saved definition at its exact revision, including when the form has unsaved edits. Choose a new `.json` filename under a configured `OutputRoots` directory; existing files are preserved. The template contains the name, exact tag/program/member selections, backend preference, interval and history limit. It excludes source paths/hashes, source artifact/watch IDs, controller targets/routes, Online/Offline mode, values and recordings. Names and tag selections may still contain application information: inspect them before sharing.

Choose **Import template…**, open the JSON file and select a registered Logix snapshot. ACD projects must already have been registered through the separately configured SDK conversion path. Templates have no destination source or target. Offline is the default binding mode; a `LibPlcTag` preference requires an explicit Online/configured-target binding or an SDK override.

Review the program mapping grid. Unchanged scopes keep their exact names. A blank destination means controller scope; controller-to-program and program-to-controller mappings are explicit and remain subject to the configured policy. Tag names, keys, member selectors and array indices are preserved; there is no wildcard or substring substitution.

Choose **Preview binding**. The destination selection grid, source hash, review hash and diagnostics show exactly what will be imported. **Preflight details…** opens the ordinary watch preflight viewer. Any input change invalidates the displayed preview. **Create stopped watch list** creates a new independent list and source snapshot; it does not update an existing list, copy history, enable controller access or start polling. Repeating a successful import creates another list; do not automatically retry an import whose response was lost without first inspecting saved lists.

## Schema

[WatchTemplateDemo.json](../examples/WatchTemplateDemo.json) binds to [WatchDemo.L5X](../examples/WatchDemo.L5X) without program remapping:

```json
{
  "schemaVersion": 1,
  "name": "Reusable process watch",
  "tags": [
    { "key": "count", "tagName": "Record", "programName": "WatchProgram", "memberPath": "Inner.Count" }
  ],
  "intervalMs": 1000,
  "historyLimit": 100,
  "backend": "Sdk"
}
```

Schema 1 accepts 1–64 distinct keys, the normal exact tag/member selector syntax, intervals 100–86400000 ms, history limits 1–1000 and `Sdk`/`LibPlcTag` preferences. JSON is limited to 256 KiB and 16 nesting levels. Field names are case-sensitive camelCase. Unknown or duplicate fields, including nested runtime data, are rejected. Future schemas are rejected explicitly. Normalizing the validated DTO produces the canonical template hash, so formatting/property order does not identify a different binding. A raw external file hash may differ from that canonical hash. Exported artifact hashes identify the actual exported bytes.

## REST and MCP

| Operation | Endpoint | Extension effect/name |
|---|---|---|
| Export exact saved definition | `POST /api/watches/{id}/template` | `apply` / `studio_watch_template_export` |
| Preview template binding | `POST /api/watch-templates/preview` | `read` / `studio_watch_template_preview` |
| Import reviewed binding | `POST /api/watch-templates/import` | `apply` / `studio_watch_template_import` |

Export body is `{ "expectedRevision": 0 }`. The result is a standard JSON artifact. Use the existing artifact download/save operations; saved files must have a `.json` extension, use `OutputRoots` and have a new filename.

Preview body:

```json
{
  "template": { "schemaVersion": 1, "name": "Reusable watch", "tags": [{ "key": "count", "tagName": "Record", "programName": "SourceProgram", "memberPath": "Inner.Count" }] },
  "projectId": "REGISTERED_DESTINATION_ID",
  "expectedSha256": "EXACT_64_CHARACTER_MANAGED_PROJECT_SHA256",
  "name": "Imported watch",
  "programMappings": [{ "sourceProgram": "SourceProgram", "destinationProgram": "WatchProgram" }],
  "mode": "Offline",
  "backend": "Sdk"
}
```

Omit `name`/`backend` to keep template preferences. Online bindings additionally require an explicit configured `controllerTargetId`. Mapping source scopes must occur in the template and cannot be repeated. Null scope values denote controller scope; empty program names are invalid. Mappings do not rename destination declarations.

The preview returns `proposed`, `templateSha256`, `reviewSha256`, `canImport`, `importBlockers` and the complete [preflight](WATCH_PREFLIGHT.md). Preview writes no artifact/list. All mapped selections must be supported by the saved declarations. Unknown SDK member templates remain deferred and block a checked import; import does not guess a layout or silently drop entries. Source/hash, target policy, CIP route and combined-budget failures also block import. Missing reader software and disabled controller access remain polling constraints and do not prevent creating a valid stopped list. Accordingly, `canImport` can be true while polling preflight is blocked.

Import body is `{ "preview": { ...the exact preview request... }, "expectedReviewSha256": "REVIEW_SHA256_FROM_PREVIEW" }`. The service parses and preflights again, checks the review hash and destination snapshot, then uses the existing browser/watch creation path. A changed template, name, backend, target, mode, mapping or source hash requires another preview when it changes the proposed binding. No preview is a grant of controller authorization. Start polling separately using the existing gates and scope rules.

Through the stable MCP bridge, pass `{ "watchId": "...", "request": { "expectedRevision": 0 } }` to the export operation. Preview/import operations take `{ "request": { ...their REST body... } }`. Discover their current generation/schema before invoking them. No fixed MCP tool was added.

Generated unit, REST/MCP and off-screen GUI checks cover this workflow. No controller was used to validate template operations; native/live reader compatibility remains separate.
