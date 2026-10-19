# REST API

Default base URL: `http://127.0.0.1:5487`. Read `.local/connection.json` for the running URL and credential file path. Set `Authorization: Bearer <contents of service.key>` on requests. Keep credentials out of chat, source control and logs. `GET /health` is the only unauthenticated endpoint.

Exploded import/export compatibility and stable rejection codes are documented in [EXPLODED_PROJECTS.md](EXPLODED_PROJECTS.md). The same checks apply through REST, fixed MCP tools, extension proxies and the desktop.

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
