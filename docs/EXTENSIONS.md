# Reloadable extensions

Version 0.7 adds a fixed MCP bridge and versioned local extension workers. After the **one-time installation/refresh of this bridge**, new extension operations, argument schemas and schema-based GUI forms can be discovered without restarting Codex or reconnecting its MCP session. The existing 46 tools remain compatible; ten new `studio_extensions_*` tools provide the stable interface.

```text
Codex / desktop GUI
        |
fixed MCP tools / authenticated REST
        |
shared ExtensionHost ---- generation leases and background jobs
        |
versioned extension worker processes
        |
shared ProjectService APIs ---- preserved projects, scope gates, SDK jobs, watches
```

The shared service owns loading, validation, version selection, dispatch, job persistence and audit. Extensions contain discoverable operations and may compute new results or compose existing project APIs. Current domain primitives and native SDK bindings remain in Core/the native bridge. Changing those primitives still requires publishing/restarting the service or native bridge when idle; an unchanged MCP bridge can continue using the same address/token. Changes to the bridge contract, plugin manifest, authentication/address or bundled skills may require client refresh/restart. This is application-level extension reload, independent of Codex's own MCP configuration reload mechanism.

## First installation

After current SDK jobs and watch readers have stopped, publish the host/bridge/GUI/worker and initial extension packages:

```powershell
.\scripts\Stop-Service.ps1
.\scripts\Build.ps1 -WithSdk
.\scripts\Start-Service.ps1 -Background
```

Refresh/reconnect the MCP server in the client, or restart Codex once if necessary, so the ten bridge tools become available. This repository does not edit global Codex configuration or attempt to control Codex's own app-server. Later extension changes use the commands below. Never republish running executables or interrupt controller work for an extension update.

`Build.ps1` publishes `ExtensionWorker` and builds four initial packages: `logix`, `factorytalk`, `viewdesigner`, and `watches`. The v0.28 packages expose 142 operations, including native HMI language workspaces/reviewed translation edits and multilingual documentation extraction/reviewed updates, logic tracing/captured rung explanation, triggered diagnostic recordings, saved trend results, three-source logic/value merging, native compatibility workflows, comparison annotations/progress, retained comparison reports, portable merge mappings, reviewed comparison-session recovery, paged value-merge discovery, exact comparison navigation, offline tag value merging/authorized upload capture, independent side-by-side ladder comparison, browser/recording, reviewed watch source rebinding, portable watch templates, read-only watch preflight, offline CIP read planning, project comparison, PLC tag where-used, ladder previews and explicit-context FactoryTalk reference resolution. The fixed MCP adapter exposes 59 tools, including three read-only broker capability/workflow readers; see [Codex integration](CODEX_INTEGRATION.md). Rebuilding identical package bytes reuses the installed version. Changed package bytes require a new `-ExtensionVersion`; previously loaded versions are immutable. A normal service startup restores each package's `active.json` selection lazily when the catalog is first requested. Startup/load errors appear in `loadErrors` and do not remove other active packages. Watch lists retain their existing stopped-after-service-restart behavior.

The v0.28 settings editor and explicit controller/file access policy require the updated Core/service and desktop host. Save the configuration separately from deploying binaries; saved changes apply at the next service start. Compatible extension reload does not apply pending host settings. See [settings and local access](SETTINGS.md).

The v0.8 browser/recorder needs new shared-service primitives and a GUI/worker update. Publish those when idle once, then activate the v0.8 packages. The ten MCP dispatch tools and protocol version remain unchanged. The updated worker accepts replies up to 12 MiB characters and depth 128, accommodating bounded recording pages; requests remain limited to 2 MiB and invocation arguments to 1 MiB. Future compatible extension changes can still reload independently.

The v0.23 diagnostics/trend/merge/compatibility operations also add Core primitives and desktop controls. Update the service/GUI when idle before activating these packages. They use the existing stable MCP dispatch contract; installing extension DLLs alone cannot add missing host endpoints. See [diagnostic workflows](DIAGNOSTICS.md).

The v0.24 multilingual documentation editor likewise needs its updated Core/service and desktop host once. Its four operations retain the stable MCP contract; browse/preview use read dispatch, while apply/export use apply dispatch. Future compatible extensions can still reload without replacing the host. See [multilingual documentation](PROJECT_DOCUMENTATION.md).

The v0.17/v0.18 value merge, paged discovery and integrated ladder comparison/navigation add shared-service endpoints and desktop views. Update the service/desktop when idle before activating these packages; reloading a worker cannot add missing host endpoints. The existing fixed MCP dispatch surface remains unchanged.

## Update and reload

For a complete stopped-host installation/update, use root `Install.cmd`. It passes `-InstallExtensions` to `Build.ps1`, which uses a SHA-256-derived suffix such as `0.28.0-build.<20 hex characters>` for the four bundled packages and selects them for the next startup after publishing the requested applications. Identical package content reuses its version; changed DLLs, PDBs or Git revision metadata produce a different version. Old versions are retained. The running-application guard applies before publishing and before selecting versions. The host still validates hashes, contracts and worker catalogs before loading a selected package. `-SkipBuild` leaves version selections unchanged.

For extension-only development, `Build-Extensions.ps1 -ContentVersion` creates the same content-qualified packages without switching existing selections. Use the printed version with `Reload-Extension.ps1` or the GUI. Omit `-ContentVersion` when publishing an explicit release version; changed bytes still require a new explicit version. `-SelectOnStartup` selects the bundled packages only when this checkout's applications are stopped. Other extension IDs are left alone.

Build changed extension code into a **new publish directory**, then install a new version. For the bundled modules:

```powershell
.\scripts\Build-Extensions.ps1 -Version 0.7.1
.\scripts\Reload-Extension.ps1 -Id viewdesigner -Version 0.7.1
```

`Build-Extensions.ps1` builds/packages extension DLLs without republishing the running service, MCP executable, GUI or worker. It installs all four bundled module versions; reload each module you intend to activate. It never overwrites an installed version; its default mode preserves existing startup selections. `Reload-Extension.ps1` calls the authenticated shared service. Supply `-ExpectedGeneration` from the catalog when coordinating concurrent updates.

From Codex, ask:

> Reload the installed viewdesigner extension version 0.7.1, refresh the extension catalog, describe the screen-summary operation and use its current generation.

The bridge tools are:

| Tool | Purpose |
|---|---|
| `studio_extensions_catalog` | Read current packages, generations, operation summaries/effects, active/draining calls and load errors; optionally filter by extension ID |
| `studio_extensions_describe` | Read one operation's exact current schema/effect |
| `studio_extensions_read` | Invoke only a declared read operation |
| `studio_extensions_preview` | Invoke only a preview/planning operation |
| `studio_extensions_apply` | Invoke only an operation that changes managed state |
| `studio_extensions_control` | Invoke control-class SDK, polling, controller or native-launch operations |
| `studio_extensions_reload` | Validate and activate an installed trusted local package |
| `studio_extensions_start_job` | Queue an operation with a pinned generation and declared effect |
| `studio_extensions_get_job` | Read a job; omit its ID to list the latest 100 |
| `studio_extensions_cancel_job` | Request cancellation; no rollback |

Always read the current catalog/description. A call includes:

```json
{
  "extensionId": "viewdesigner",
  "operation": "viewdesigner_screen_summary",
  "generation": "<generation returned by the live catalog>",
  "arguments": {
    "projectId": "<registered View Designer export ID>",
    "file": "User-Defined Screens/Overview.hmi"
  }
}
```

The host rejects stale generations, wrong effect channels and arguments outside the declared schema before dispatching to a worker. Refresh after `stale_generation`, then review the current operation before resubmitting. A successful reload is not authorization to connect to a controller. SDK target/method/program/routine restrictions, controller gates, source hashes and private-copy rules still execute in the existing shared APIs. The module's `studio_sdk_start_job` is conservatively classified as control, including offline SDK work. A returned SDK job is separate from the extension invocation job: poll it using the SDK job API.

In the desktop GUI, open **Extensions…**. Refresh the catalog, choose an extension/operation and fill the generated fields. Scalar strings use plain text; objects, arrays, booleans and numbers use JSON. Optional blank fields are omitted. Use **Run as pinned background job** for longer calls, then **Read job** to inspect the result. Select/type an installed extension ID and version, then **Reload extension**; the form refreshes its catalog and controls. This also loads newly installed extension IDs into an already running host.

## Activation, jobs and rollback

The host copies the manifested files into a private cache, verifies every SHA-256, checks identity/contract/schema/effects, starts a candidate worker and reads its catalog. It switches new requests only after those checks succeed. Failure leaves the selected old worker available. Source packages are not executed in place. Entry paths/IDs cannot be supplied as arbitrary shell commands or URLs.

Calls acquire a generation lease before dispatch; background jobs acquire their lease when queued. A reload marks a leased old worker `draining`, and that worker survives until its last call finishes. Active SDK jobs and watch sessions remain owned by the shared Core/native bridge and are not stopped by an extension reload. Jobs store generation/status/results but not argument payloads; request arguments remain in memory/worker pipes. Results can contain project information and stay in protected local state. Audit records identify operation/version/generation without logging argument bodies.

To roll back, reload the previous installed version. Calls already running continue on their original version. The new selection receives a new generation, so old client call records are rejected. An invocation error is not a rollback guarantee. Timeout/cancellation of an executing worker can interrupt it, fail other calls queued on that worker and leave completed external actions in place. Interrupted jobs become `indeterminate`, including across service restart; there is no automatic retry or reconnect. Inspect actual effects before retrying.

Current bounds: at most 16 active/draining workers, 32 active extension jobs, 128 operations per package, 256 manifested files/128 MiB total/64 MiB per file, 256 KiB manifest, 1 MiB argument JSON, 2 MiB request / 12 MiB reply character limits and schema depth 24. Worker calls serialize within one generation; different extension/version workers can run concurrently. Invocation timeout defaults to 240 seconds, configurable up to 1,800 seconds. Use a background job when the transport's own timeout could expire sooner. Package caches, job records and audit files are retained; no automatic cache deletion runs.

Extensions are **trusted local executable code** with the current Windows user's privileges. Process isolation provides separate dependencies and lifetimes, not a security sandbox. Effects describe the extension author's contract; the host checks dispatch channel/schema and the bundled modules preserve existing service gates. Package hashes detect changed bytes, not publisher identity or malicious behavior. Install code you trust.

## Author an extension

Create a .NET 8 class library referencing `src/Studio5000.Extensibility/Studio5000.Extensibility.csproj`. Implement `IWorkspaceExtension` with a public parameterless constructor, a side-effect-free `Describe()` and `InvokeAsync(...)`. Keep initialization/catalog discovery free of native or controller actions. The worker exposes the package directory and workspace root through `ExtensionContext`; bundled modules use `ServiceClient` to call the single shared service instead of opening separate project state.

```csharp
public sealed class MyExtension : IWorkspaceExtension
{
    public ExtensionOperation[] Describe() => [new("example_count", "Count supplied values", "read",
        JsonSerializer.SerializeToElement(new {
            type = "object",
            properties = new { values = new { type = "array", items = new { type = "number" } } },
            required = new[] { "values" }, additionalProperties = false
        }))];

    public Task<JsonElement> InvokeAsync(string operation, JsonElement arguments,
        ExtensionContext context, CancellationToken ct) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new {
            count = arguments.GetProperty("values").GetArrayLength()
        }));
}
```

Use `System.Text.Json` and `Studio5000.Extensibility`. Supported input-schema keywords are `type` (including nullable type arrays), `properties`, `required`, `additionalProperties` (boolean/schema), `items`, `enum`, and `description`. Unsupported keywords such as `$ref`, `oneOf`, or `pattern` cause candidate rejection. Validate domain semantics inside the operation/shared API. Declared effects must be exactly `read`, `preview`, `apply`, or `control`. Operation/extension/version identifiers are case-sensitive.

Publish/install explicitly:

```powershell
dotnet publish C:\YourExtension\YourExtension.csproj -c Release -o C:\YourExtension\publish-1.1.0
.\scripts\Publish-Extension.ps1 -Id my-extension -Version 1.1.0 `
    -PublishedDirectory C:\YourExtension\publish-1.1.0 `
    -EntryAssembly YourExtension.dll -EntryType YourNamespace.MyExtension
.\scripts\Reload-Extension.ps1 -Id my-extension -Version 1.1.0
```

The package layout is `extensions/<id>/versions/<version>/manifest.json` plus all published dependencies. The manifest contains `id`, `version`, `contractVersion: 1`, `entryAssembly`, `entryType`, and a `files` map from relative paths to SHA-256 values. The script creates `active.json` only for a new module; later activation belongs to reload. Keep installed versions separate and never edit a loaded package in place. Generated packages and private caches are ignored by Git; source code and build scripts are committed.

## REST and validation

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/extensions` | Live catalog; optional `extensionId` filter and `includeSchemas=false` for compact summaries |
| GET | `/api/extensions/operation?extensionId=...&operation=...&generation=...` | Exact descriptor |
| POST | `/api/extensions/reload` | `extensionId`, optional `version` and `expectedGeneration` |
| POST | `/api/extensions/invoke/{effect}` | Generation-pinned call |
| GET/POST | `/api/extensions/jobs` | List/queue jobs (`effect`, `call`) |
| GET | `/api/extensions/jobs/{id}` | Read status/result |
| POST | `/api/extensions/jobs/{id}/cancel` | Request cancellation |

State is under `.local/state/extensions/`: private package snapshots, immutable-version hash receipts, `job-*.json` and `audit.jsonl`. These use the same protected state directory as the shared service.

`scripts/Verify-Isolated.ps1` builds two different fixture DLLs, starts an isolated service with controller execution disabled, and uses one MCP client connection to reload v1→v2, discover/run a newly compiled operation, retain an in-flight job on v1, reject a bad reload and roll back. It also checks the bundled HMI summary and wrong-effect rejection. This demonstrates worker/MCP reload behavior; native HMI validation and controller runtime behavior remain separate. The desktop render is a generated display fixture, not native Studio automation.

`scripts/Verify-InstallerPackages.ps1` exercises the packaging scripts in an isolated checkout using generated file payloads. It checks identical/changed rebuilds, selection only after all packages pass, preserved rollback bytes and explicit-version rejection. It starts no worker or service and can run while the real workspace is in use. These file-level checks complement the compiled-worker tests above.

The v0.25 HMI language editor adds shared Core/service primitives and a desktop form. Deploy the updated host and GUI once when idle; its 16 new extension operations retain the stable MCP contract. See [HMI multilingual text](HMI_LANGUAGES.md).

The v0.26 host adds bounded concurrent workspace coordination and durable offline transactions. Bundled stateless proxy modules opt into up to four process workers per generation; legacy/custom packages default to one. New primitives require updating the idle service/GUI once; the fixed MCP bridge remains unchanged. See [concurrent requests and transactions](CONCURRENCY_TRANSACTIONS.md).
