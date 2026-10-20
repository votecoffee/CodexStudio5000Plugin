# REST API

The authenticated settings editor endpoints and saved/effective configuration contract are documented in [SETTINGS.md](SETTINGS.md). Saving stages changes for restart and leaves polling/jobs unchanged.

App metadata and named versioned workflow readers are described in [CODEX_INTEGRATION.md](CODEX_INTEGRATION.md). Their authenticated REST/MCP routes use shared Core and grant no native access.

Default base URL: `http://127.0.0.1:5487`. Read `.local/connection.json` for the running URL and credential file path. Set `Authorization: Bearer <contents of service.key>` on requests. Keep credentials out of chat, source control and logs. `GET /health` is the only unauthenticated endpoint.

Read-only project review uses `POST /api/projects/compare` and `POST /api/projects/{id}/ladder-preview`. Both require snapshot hashes; the `logix` extension exposes `studio_compare_projects` and `studio_ladder_preview` through `studio_extensions_read`. See [PROJECT_REVIEW.md](PROJECT_REVIEW.md) for contracts, normalization rules, parser coverage and limits. Fixed MCP tool names remain unchanged.

The comparison browser uses `POST /api/projects/compare/routines` and `/api/projects/compare/ladder` (`studio_compare_routines`, `studio_compare_ladder`, read effects). Reviewed whole-tag value transfer uses `/api/value-merge/preview` and `/api/value-merge/apply`; an optional `/api/value-merge/capture` queues an authorized upload-only SDK job. See [value merge](VALUE_MERGE.md) for exact mappings, declaration/value checks, offline export and online capture gates.

`POST /api/projects/compare/locate` resolves exact selected rung/routine XPaths into their aligned ladder page (`studio_compare_locate`, read). `POST /api/value-merge/browse` searches/pages candidate values without an apply review (`studio_value_merge_browse`, read). Both require exact source snapshot hashes; see the guides above for request contracts and limits.

`POST /api/projects/{id}/tag-references` provides hash-bound lexical PLC where-used candidates for a controller/program base tag. The read extension operation is `studio_tag_references`; see [TAG_REFERENCES.md](TAG_REFERENCES.md) for scope, source locations, indirect indices and limits.

Exploded import/export compatibility and stable rejection codes are documented in [EXPLODED_PROJECTS.md](EXPLODED_PROJECTS.md). The same checks apply through REST, fixed MCP tools, extension proxies and the desktop.

`POST /api/projects/{id}/graphics-references` performs hash-bound read-only FactoryTalk reference extraction. Body: `{ "expectedSha256": "...", "query": "optional substring", "offset": 0, "limit": 200 }`. The `factorytalk` extension exposes it as `ftview_tag_references` through `studio_extensions_read`; it is not a new fixed MCP tool. See [FactoryTalk reference coverage and limits](FACTORYTALK.md#tag-references).

`POST /api/projects/{id}/graphics-reference-resolution` checks references against hash-bound saved Logix snapshots with explicit shortcut mappings and scoped parameter values. The extension operation is `ftview_resolve_references` (read). See [context contracts, statuses and limits](FACTORYTALK.md#plchmi-reference-resolution).

[EXTENSIONS.md](EXTENSIONS.md) documents the stable `/api/extensions` catalog, describe/reload/invoke and pinned-job endpoints. These let installed extension features change while the MCP bridge stays connected, with separate read/preview/apply/control dispatch channels.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/capabilities` | Roots, formats, editing/native status |
| GET/POST | `/api/projects` | List/register a saved project |
| POST | `/api/projects/from-exploded` | Register recomposed l5xplode directory |
| GET | `/api/projects/{id}` | Current manifest and hash |
| GET | `/api/projects/{id}/elements` | Filter `kind`, `query`; page `offset`, `limit` |
| GET | `/api/projects/{id}/element?xpath=...` | Read exact component XML |
| POST | `/api/projects/{id}/previews` | Validate and persist a draft |
| GET | `/api/projects/{id}/previews/{draft}` | Read draft record |
| POST | `/api/projects/{id}/previews/{draft}/apply` | Select a new revision |
| POST | `/api/projects/{id}/exports` | Export `l5x`, `acd` or `explode` |
| GET | `/api/artifacts/{id}` | Download hash-bound artifact bytes |
| POST | `/api/artifacts/{id}/save` | Save a new file under OutputRoots |
| GET | `/api/audit?projectId=...` | Last 500 matching local audit events |
| GET | `/api/sdk/capabilities` | Full version-specific SDK operation/argument/enum catalog |
| GET | `/api/sdk/coverage` | Installed public-signature and compiled-binding parity |
| POST | `/api/sdk/source` | Hash an allowed SDK source/import file |
| POST | `/api/sdk/online-edits/plan` | Prepare an online import and native Test/Assemble handoff; never connects |
| GET/POST | `/api/sdk/jobs` | List jobs / submit ordered native SDK calls |
| GET | `/api/sdk/jobs/{id}` | Per-call results, diagnostics, status and output artifacts |
| POST | `/api/sdk/jobs/{id}/cancel` | Request cancellation; poll completion; no rollback |
| GET/POST | `/api/watches` | List saved watch lists / create a stopped list |
| POST | `/api/projects/{id}/cip-watch-plan` | Hash-bound offline symbolic read plan; never connects |
| POST | `/api/watches/preflight` | Read-only draft/saved/browser preflight; exactly one settings/watchId/browser |
| GET | `/api/watches/{id}/preflight` | Inspect a saved definition and source without starting polling |
| POST | `/api/watches/{id}/template` | Export a portable JSON artifact at expectedRevision |
| POST | `/api/watches/{id}/rebind-preview` | Compare approved old declarations with a hash-bound destination |
| POST | `/api/watches/{id}/rebind` | Apply a reviewed stopped-list source update at expectedRevision |
| POST | `/api/watch-templates/preview` | Check exact project/program bindings without creating state |
| POST | `/api/watch-templates/import` | Recheck the review hash and create a new stopped list |
| GET/POST | `/api/watches/{id}` | Read status/latest/types / update stopped settings with expectedRevision |
| POST | `/api/watches/{id}/start` | Start interval reads; online access requires an enabled allowed target |
| POST | `/api/watches/{id}/stop` | Stop polling and retain stale cached values |
| POST | `/api/watches/{id}/delete` | Delete a stopped list's definition |
| GET | `/api/watches/{id}/history?limit=100` | Bounded in-memory polling history |
| POST | `/api/structures/pull` | Start a fresh controller upload for UDT/schema extraction |
| POST | `/api/structures/extract` | Extract definitions and nested tag values from projectId or successful jobId |

[VIEW_DESIGNER.md](VIEW_DESIGNER.md) documents the separate `/api/viewdesigner` export-folder API, edit shape and native import boundary.

SDK request/response examples, target configuration and evidence boundaries are in [SDK.md](SDK.md). These endpoints use the same shared service as XML editing and FactoryTalk.

[WATCHES.md](WATCHES.md) covers watch request/response shapes, intervals, UDT extraction, scope policy and native decoding. Reading cached watches never connects; starting online polling or pulling structures requires controller authorization/access.

[WATCH_PREFLIGHT.md](WATCH_PREFLIGHT.md) covers local declaration diagnostics, stale sources, reader configuration and partial/combined CIP budgets. Preflight never saves, starts polling or launches a reader.

[WATCH_TEMPLATES.md](WATCH_TEMPLATES.md) covers portable schema 1, export fields, program mappings and hash-bound import. Templates contain no source/target binding or live data; imports never start polling.

[WATCH_REBIND.md](WATCH_REBIND.md) covers source refresh for the same list, declaration-change flags, unavailable baselines and retained recordings by revision. Rebind does not start polling or modify controller target settings.

Register and edit using PowerShell:

```powershell
$repo = (Get-Location).Path # Run from the RockwellMCPServer checkout root.
$connection = Get-Content -LiteralPath "$repo\.local\connection.json" -Raw | ConvertFrom-Json
$headers = @{ Authorization = 'Bearer ' + (Get-Content -LiteralPath $connection.tokenFile -Raw).Trim() }
$project = Invoke-RestMethod -Uri ($connection.url + '/api/projects') -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ path = "$repo\examples\Demo.L5X" } | ConvertTo-Json)
$request = @{
    expectedSha256 = $project.currentSha256
    edits = @(@{ operation = 'set_text'; xPath = "//Tag[@Name='CycleCount']/Description"; value = 'Completed production cycles' })
} | ConvertTo-Json -Depth 8
$draft = Invoke-RestMethod -Uri ($connection.url + "/api/projects/$($project.id)/previews") -Method Post -Headers $headers -ContentType 'application/json' -Body $request
$draft.diff
$updated = Invoke-RestMethod -Uri ($connection.url + "/api/projects/$($project.id)/previews/$($draft.id)/apply") -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ expectedSha256 = $draft.baseSha256 } | ConvertTo-Json)
```

For `replace_element` and `add_element`, `value` is one XML element, not a complete L5X wrapper. Read a component before replacing it. Use `set_text` on a Description/Text/Comment XPath for scalar updates; `set_attribute` additionally takes `attribute`. The preview records the selected before XML and requested value. Serialization may normalize XML declaration/encoding; inspect the exported result if byte representation matters. Original bytes are retained.

Export body: `{"expectedSha256":"...","format":"acd"}`. Save body: `{"path":"C:\\...\\artifacts\\exports\\Machine_edited.acd"}`. The output extension must match the artifact. There is no overwrite flag.

Failures include `401 unauthorized`, `403 origin_denied`, `404 not_found`, `409 stale_revision`/state mismatch, and `400` for invalid edits, paths, native failures or unavailable native support. On stale revision, read the latest project and create a new preview. Native failures include diagnostics but never return a successful artifact. HTTP requests are limited to 2 MiB; imported project files to 64 MiB; returned components to 1 MiB. Inspection pages contain at most 1,000 elements.

## Multilingual documentation

`POST /api/projects/{id}/documentation/browse`, `/preview`, `/apply` and `/export` share language-aware extraction and reviewed updates. Corresponding `studio_documentation_*` extension operations use read for browse/preview and apply for revision/artifact creation. They preserve other translations, executable content and values. See [documentation fields, language identity, request examples and limits](PROJECT_DOCUMENTATION.md).

## Diagnostic and trend workflows

The shared service exposes these REST families through reloadable extension operations. See [diagnostics and trends](DIAGNOSTICS.md) for request fields, instruction coverage, limits and step ordering.

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/projects/{id}/logic/trace` | Hash-bound reader/writer and dependency graph |
| GET / POST | `/api/diagnostics/captures` | List or create immutable captured values |
| POST | `/api/diagnostics/evaluate` | Explain a captured program RLL rung |
| GET / POST | `/api/diagnostics/triggers` | Trigger inventory or revision-checked definition save |
| POST | `/api/diagnostics/triggers/{id}/arm`, `.../{id}/delete` | Local arm/disarm or definition removal |
| GET | `/api/diagnostics/triggers/{id}/episodes/{episodeId}` | Immutable pre/post episode with source revision |
| POST | `/api/diagnostics/triggers/{id}/episodes/{episodeId}/capture` | Freeze a sampled frame for explanation |
| GET / POST | `/api/trends` | List summaries or save a named view |
| GET | `/api/trends/{id}` | Read exact saved trend definition |
| POST | `/api/trends/{id}/delete`, `/api/trends/analyze`, `/api/trends/export` | Remove view, gather result page or export exact result |
| POST | `/api/logic-value-merge/preview`, `/api/logic-value-merge/apply` | Whole-project preserve-fresh or legacy selected-tag merge into a new project |
| POST | `/api/legacy-review/preview`, `/api/legacy-review/prepare` | Exact reviewed v35-to-v32 legacy L5K input and manifest from a completed native export; no SDK job started |
| GET | `/api/logix-validation/profiles` | Named checks, scope, budgets and unsupported acceptance claims |
| POST | `/api/logix-validation/preview`, `/api/logix-validation/record` | Exact policy/hash-bound checks and retained immutable JSON receipts |
| GET | `/api/logix-validation/receipts/{id}?expectedSha256=...` | Verify/read a retained receipt without rerunning checks |
| POST | `/api/version-compatibility/preview`, `/api/version-compatibility/start` | Plan or start separate offline native target-version copy |
| GET | `/api/version-compatibility/{id}` | Conversion/readback state and configuration report |
| POST | `/api/version-compatibility/{id}/readback` | Reopen successful converted ACD in a separate native job |

Creating/editing views, capturing existing values, arming triggers and gathering results never start polling. Native compatibility start/readback use the extension control channel; read-only dispatch cannot create artifacts or projects. Result and merge apply requests require their exact review hashes.

## FactoryTalk ME/SE endpoints

FactoryTalk uses the same registration, inspection, preview/apply, XML export and artifact-save endpoints. Register a `.xml` file rooted at `gfx`; `kind` identifies `ftview-me` or `ftview-se`. `set_attribute`, `set_text` and shape-preserving `replace_element` edit existing properties. Export with `format="xml"`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/factorytalk/status` | Installed schemas, launcher and current Studio window titles |
| POST | `/api/factorytalk/launch` | Request the installed Studio launcher; no arbitrary arguments |
| GET | `/api/projects/{id}/graphics-validation` | Declared schema name/hash and validation status |
| POST | `/api/projects/{id}/graphics-imports` | Prepare a fixed-target update import plan and ZIP |
| GET | `/api/factorytalk/imports/{planId}` | Read the persisted plan and local native file paths |
| POST | `/api/factorytalk/imports/{planId}/verify` | Compare changed properties in supplied re-export XML |

Prepare body:

```json
{"expectedSha256":"...","applicationName":"MachineHMI","displayName":"Main","language":"en-US"}
```

The response includes `xmlPath`, `batchPath`, `backupPath`, `reexportPath`, changed-property checks, schema hash and a standard ZIP artifact. Application and language are explicit caller-supplied context, not extracted proof. The wizard must target that same context. A plan requires at least one applied change. The original XML is retained separately from native graphics backups.

Verify body is `{}` for the plan's expected re-export path, or `{"reexportPath":"C:\\AllowedSourceRoot\\Main.xml"}` for another allowed source file. `status` is `property_values_match` or `property_values_mismatch`. In both cases `nativeImportStatus` stays `not_observed_by_service`; a matching supplied file alone does not prove Studio import. Missing export, edition mismatch and stale revision are explicit errors. Stable object-name selectors tolerate native object-order changes while checking the intended properties.

Actual native interaction is supplied by the packaged `factorytalk-studio` skill and available Computer Use tools. API-only callers receive prepared files and verification results, not an unattended Studio UI automation endpoint.

Any local agent/API client can call this REST API. A cloud OpenAI API request cannot directly reach the user's loopback address; run the tool execution loop locally and forward function calls to these endpoints, or use the local MCP transport. No cloud exposure is configured by this project.
