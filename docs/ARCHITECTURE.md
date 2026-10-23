# Architecture

RockwellMCPServer is a standalone application and MCP server. Its [app-owned Codex plugin and skills](CODEX_INTEGRATION.md), Windows GUI and REST clients use the same authenticated loopback service. Domain operations and policy belong in `Studio5000.Core`; frontends retain presentation state rather than separate engineering mutations.

[CodexMCPBroker](https://github.com/votecoffee/CodexMCPBroker) maintains two separate offerings: a shared Python application framework and an independent Windows MCP Service Manager. Rockwell's [optional framework host](FRAMEWORK.md) is an explicitly selected adapter; [management integration](SERVICE_MANAGEMENT.md) reports activity/version through app-owned contracts. Neither replaces Rockwell's standalone plugin/skills or requires a universal plugin. The `universal-codex-tools`/`universal_tools` names remain compatibility identifiers. Upstream releases do not silently replace reviewed app pins.

```text
Codex plugin/skills -- owned stdio frontend + MCP adapter --+
Windows desktop ----- shared HTTP client -----------------+-- authenticated service
Other local API clients ---------------------------------+           |
                                                               shared Core
                                            snapshots / edits / reviews / artifacts
                                             |             |             |
                                       XML + L5xploder  SDK worker    CIP worker
                                                           |
                                                   local SDK session
```

## Components and lifetime

| Component | Responsibility |
| --- | --- |
| `Studio5000.Core` | Source/revision policy, byte-preserving and structural edits, reviews/transactions/validation, SDK jobs, watches/trends/diagnostics, multilingual PLC/HMI exports and receipts |
| `Studio5000.Service` | One shared workspace owner per state directory, authenticated REST, staged settings and shutdown fences |
| `Studio5000.Client` | Common GUI/MCP HTTP transport and structured errors |
| `Studio5000.StdioHost` / `Studio5000.Mcp` | Surviving transport owner, ordinary MCP SDK adapter and fixed discovery/read/preview/apply/control tools; no independent mutations |
| `Studio5000.Extensibility` / `ExtensionWorker` | Immutable packages, schemas/effects, generation pinning, bounded worker pools and draining/reload |
| `Studio5000.Extensions.Rockwell` | Reloadable Logix, FactoryTalk, View Designer and watch catalogs/compositions calling the shared service |
| `Studio5000.NativeBridge` | Separately compiled installed-SDK binding; exact owned worker plus independent native session/save/close/effect evidence |
| `Studio5000.CipBridge` | Optional isolated read-only libplctag reader with explicit configured targets/budgets |
| `Studio5000.Desktop` | Shared-service workflow editors and results; SDK/profile/GUI settings use common validation |

Compatible operations and canonical workflow content update behind stable discovery tools. Each extension call pins its generation; stale calls refresh and review instead of replaying a mutation. Pools are manifest-bounded and old generations drain existing calls. Host-loaded tools, plugin files and native Codex skills have separate reconnect/update requirements. See [extensions](EXTENSIONS.md), [framework guidance](FRAMEWORK.md) and [readiness](READINESS.md).

## State, preservation and concurrency

Registration snapshots allowed inputs and records source/current SHA-256 values. ACD registration converts a private copy through the SDK; XML inspection needs no SDK. Applies select immutable revisions using source/review hashes. Scalar edits retain supported source bytes; structural edits publish an explicit preservation contract. [Scoped structural reviews](../workflows/rockwell-engineering/references/structural-edits.md) preserve untouched opaque records and record producer/version information.

Artifacts are hash-checked and saved to new allowed paths. A saved snapshot is not a live controller image. Exploded registration snapshots its input before recomposition; logical equivalence does not imply byte equality. Audit/validation/transaction receipts describe their exact source and scope, not commissioning or tamper-proof compliance. Failed operations can leave retained staging/journals; do not erase them to retry.

Independent workspaces can mutate concurrently, while same-workspace writes serialize and detect revision conflicts. Offline transactions review/commit one workspace with durable receipts; they do not span native/controller effects. SDK calls retain an ordered lane. SDK jobs, watches and SDK-service recovery participate in shutdown/activity fences. Cancellation or lost results are not rollback and never authorize automatic replay. See [concurrency and transactions](CONCURRENCY_TRANSACTIONS.md).

Saved watches, UDT structures, trends/history, triggers/captures, comparison sessions and multilingual exports use bounded state/artifact stores. Polling starts explicitly and records timestamps, quality/staleness/gaps and source revisions. Rebinding and actual online acquisition remain explicit. See [watches](WATCHES.md), [browser/recording](BROWSER_RECORDING.md) and [diagnostics](DIAGNOSTICS.md).

## Ownership and native boundaries

The service and stdio owners use creation-bound identities, durable claims and private Windows jobs. Workers are created suspended, contained and durably bound before execution. Cleanup cannot adopt arbitrary PIDs or user/native applications. Native leases are independent of worker process lifetime: exact save/close/effect evidence releases them; dirty/shared/attached or uncertain resources retain barriers. Metadata retries publish the same bytes without redispatching native/process/SCM actions. See [resource ownership](RESOURCE_LEDGER.md).

`SdkServiceControl` targets only local `LdSdkService`: start Stopped, observe StartPending and leave Running unchanged. SCM owns that external service. Native GUI handoff uses the existing user desktop shell and grants no ownership of its application/documents. Native import/build/controller acceptance remains operation/version-specific and separate from cleanup or transport success.

The separately prepared [`local-project` candidate](NATIVE_LOCAL_PILOT.md) enables bounded offline SDK read/export/import/build work with dedicated paths, owner/worker profile matching, controller/CIP/SCM-start/GUI boundaries and immutable reviewed payloads. It does not activate an installed runtime or inherit controller permission. Standard controller target policies remain separate.

## Configuration and publication

`service.local.json` belongs to the selected installation. GUI/REST saves validate settings, retain private backups and stage changes for restart. Engineering gates remain independent of plugin discovery. This is a trusted single-Windows-user loopback app, not a remote multi-tenant deployment. Credentials, customer projects, receipts and host bindings are not public repository content.

The standalone package contains manifests/skills/launcher plus a runtime pointer delegating to installed checkout scripts/binaries. Keep those installed bytes stable while preparing candidates. Package, serving version/effective settings, chat tools and native acceptance are distinct [readiness layers](READINESS.md). [TOML publication](CONFIG_PUBLICATION.md) uses UTF-8 bytes, preserves LF/CRLF/BOM and refuses malformed CRCRLF/lone CR without silently repairing user text.

Release identities are fixed NY timestamps from [release-version.json](../release-version.json), with [separate display/package/.NET mappings](VERSIONING.md). Startup/build does not generate a release identity. [Contributor guidance](../CONTRIBUTING.md) covers source/fixture/package checks and privacy.
