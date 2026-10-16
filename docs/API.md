# REST API

Default base URL: `http://127.0.0.1:5487`. Read `.local/connection.json` for the running URL and credential file path. Set `Authorization: Bearer <contents of service.key>` on requests. Keep credentials out of chat, source control and logs. `GET /health` is the only unauthenticated endpoint.

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

Register and edit using PowerShell:

```powershell
$repo = 'C:\~LocalWork\Repos\Programming\~Github\CodexStudio5000Plugin'
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

Any local agent/API client can call this REST API. A cloud OpenAI API request cannot directly reach the user's loopback address; run the tool execution loop locally and forward function calls to these endpoints, or use the local MCP transport. No cloud exposure is configured by this project.
