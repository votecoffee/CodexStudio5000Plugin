# Optional symbolic CIP watch reader

The `LibPlcTag` watch backend reads authorized scalar symbols through a separate worker using [libplctag.NET](https://github.com/libplctag/libplctag.NET). It shares watch scheduling, target/program policies, recordings, stale status and trends with the default `Sdk` backend. It does not need the Logix Designer SDK to poll an existing L5X declaration snapshot. Native project editing and fresh controller uploads still use the SDK.

This adapter has generated/offline validation only. Live behavior, performance and firmware compatibility, including v35/v38, require a separately authorized controller test. Loading the packaged native library or checking a generated plan does not establish controller compatibility.

## Install and configure

```powershell
.\scripts\Build.ps1 -WithCip
.\scripts\Initialize.ps1 -EnableCip
```

These commands configure `CipBridgePath`; they do not enable controller access. Stop watch lists and restart the shared service after changing host binaries/configuration. Compatible extension-only changes can still use catalog reload.

In ignored `service.local.json`, explicitly configure the target's `CipRead` gateway and numeric route in addition to its existing Linx communications path. Example **documentation-only TEST-NET** target:

```json
{
  "Id": "authorized-plc",
  "CommunicationsPath": "YOUR_CONFIGURED_FACTORYTALK_LINX_PATH",
  "Policy": {
    "AllowedMethods": ["GoOnlineAsync", "GetTagValueAsync"],
    "ProgramName": "MainProgram"
  },
  "CipRead": { "Gateway": "192.0.2.1", "Route": "1,0", "TimeoutMs": 3000 }
}
```

The gateway must be canonical unicast IPv4. Route is 2..16 decimal bytes in port/link pairs; timeout is 100..30000 ms per library operation. Supply the route appropriate to the controller/backplane. Linx paths are never translated into CIP routes. Callers cannot provide arbitrary libplctag attributes, routes or endpoints in watch requests. `EnableControllerOperations` must be explicitly enabled before starting an authorized online watch. The existing allowed method names remain the shared policy vocabulary; this backend does not invoke those SDK methods.

## Plan reads without connecting

Register a saved L5X snapshot, then call `POST /api/projects/{id}/cip-watch-plan`:

```json
{
  "expectedSha256": "EXACT_64_CHARACTER_PROJECT_SHA256",
  "tags": [{ "key": "count", "tagName": "Station", "programName": "MainProgram", "memberPath": "Inner.Count" }]
}
```

The `studio_cip_watch_plan` read operation is discoverable in the `watches` extension. In the desktop tag browser, select tags/members and choose **Plan CIP reads (offline)**. The report contains the source hash, bounded extraction tree, exact symbolic leaf addresses, expected CIP primitive types and unique leaf count. Planning uses saved declarations and never connects, even when controller access is enabled. It does not certify live type identity.

The strict planner rejects an invalid selection. For a report containing every selection's diagnostics, configuration checks and partial/combined budgets, use [watch preflight](WATCH_PREFLIGHT.md). It is also read-only and does not remove unsupported selections automatically.

## Create a watch

Use the ordinary [watch definition](WATCHES.md) with `"backend": "LibPlcTag"`, `"mode": "Online"`, a configured `controllerTargetId`, and a saved `.L5X` source/artifact with its SHA-256. The GUI exposes the reader choice in watch settings and browser watch creation. Lists are saved stopped. Saving validates the entire plan without opening the worker. Start/stop, recordings, history and trends use the same service operations as SDK watches. Existing definitions default to `Sdk`.

The start path checks the enabled controller gate and program policy, preserves and hash-checks a private source copy, and validates all selections before any library handle is initialized. Each poll reads only the planned scalar symbols. Duplicate symbols are read once per cycle and reconstructed into separate nested JSON values. Live primitive type bytes must match the saved declaration before decoding; an unavailable type attribute also fails. Missing members, incompatible types, transport errors or invalid replies fault the whole list and make the last sample stale. The service does not fall back to SDK or retry a faulted session automatically; inspect and refresh the snapshot before an explicit restart. The underlying library manages its connections during an active read session.

## Coverage and limits

Supported declarations: BOOL/BIT, SINT/INT/DINT/LINT, USINT/UINT/UDINT/ULINT, REAL/LREAL, bounded arrays and exported UDT members. Multi-dimensional selectors such as `[1][2].Count` become CIP `[1,2].Count`. Hidden backing members are omitted; aliases and members with `ExternalAccess=None` cannot be selected. Nonfinite floats are rejected; 64-bit integers retain their exact JSON numeric text, so clients need a suitable parser.

Strings and unavailable built-in/module/AOI templates are unsupported. Use the SDK backend for those representations. The adapter does not discover live declarations or infer UDT padding/BOOL packing. It reads member symbols individually, so changed declarations with unchanged primitive leaves cannot be detected as a whole-UDT schema change. Refresh saved metadata when declarations change. Live structure discovery remains the separate SDK upload/extraction workflow.

Each list has at most 64 selections, 256 unique scalar reads, 2048 plan nodes and 16 expansion levels. An expanded array dimension is capped at 256; select an element or smaller slice of larger arrays. Duplicate selections count toward node/selection bounds. Scalar symbols are bounded to 2048 characters. Sampling is serial; large structures can be slower than the requested interval. Values within one sample are **not an atomic controller snapshot**. No throughput benchmark has been performed.

The worker exposes no tag writes, force operations, arbitrary CIP messages, downloads, mode changes or imports. Auto-sync reads/writes are unset and read caching is disabled; only the shared scheduler requests polls. Default host/MCP/GUI builds carry no libplctag dependency. The optional worker's package lock and [third-party notices](../THIRD_PARTY_NOTICES.md) identify its separately licensed dependencies.

## Offline verification

```powershell
.\scripts\Verify-Isolated.ps1 -WithCip
```

This adds an isolated optional-worker publish, generated structure/read self-test, native library version/load check and malformed-startup protocol rejection. It never creates a controller tag handle. Core tests cover plans, bounds, scalar type checks, scope/gates, stale sources, synthetic read failures and persisted definitions. REST/MCP checks exercise the read-only plan through the same service used by the GUI. Controller acceptance remains pending.
