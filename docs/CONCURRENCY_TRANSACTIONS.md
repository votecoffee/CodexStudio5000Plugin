# Concurrent requests and offline transactions

REST, MCP and the desktop GUI share the same workspace coordinator and transaction service. Multiple clients can read immutable snapshots and edit independent workspaces concurrently. Writers to the same workspace serialize and recheck their expected hashes; an old preview cannot silently overwrite another writer.

## Scheduling

The default is **8 active workspace mutations** and **256 queued mutations**, configurable with `Studio5000.MaximumConcurrentMutations` (1..32) and `Studio5000.MaximumQueuedMutations` (1..4096) in local service settings. `GET /api/concurrency` and `studio_concurrency_status` report active/queued writers and resource keys. A full queue returns HTTP 429. Cancelling a queued request removes its reservation; it does not interrupt another request holding that resource.

Resource sets are acquired together. Earlier conflicting requests cannot be overtaken indefinitely, and legacy compound operations retain an exclusive barrier while they run. These include operations whose consistency spans multiple resources or existing native job admission paths. Native SDK worker ordering and controller policies are unchanged; this feature does not authorize controller access or promise concurrent native writes to one controller.

Committed project bytes/trees remain immutable. A short metadata I/O lease protects manifest reads/replacement on Windows, while project parsing, hashing and immutable-content reads can overlap. An atomic manifest replacement selects each committed revision. Atomic writes flush their temporary file and retry bounded transient replacement failures; this is process-restart recovery, not a hardware power-loss guarantee.

### Concurrent MCP extension calls

The fixed MCP bridge remains unchanged. Bundled stateless proxy modules opt into **up to four worker processes per generation**, created as demand increases. Requests keep their own process/response channel, and every additional worker must describe the same catalog as the validated generation. Invocation failures are never automatically replayed. Cancelling a request waiting for a pool slot does not kill another invocation.

Custom/legacy packages default to **one worker**. Only opt in with `parallelWorkers: 2..4` in the immutable manifest when independent extension instances can operate safely without shared in-process state. `Publish-Extension.ps1 -ParallelWorkers` defaults to 1; `Build-Extensions.ps1 -ParallelWorkers` defaults to 4 for the bundled proxies. The parallel-worker setting participates in content-version identity. Changing it requires a new package version. Catalog summaries report the configured parallel-worker count, which is a ceiling rather than an assertion that every worker is running.

## Transaction scope and guarantees

One transaction targets one saved workspace:

| `workspaceType` | Supported stages |
| --- | --- |
| `project` | Existing project XML operations and Logix documentation edits; XML is applied before documentation within a stage. FactoryTalk display XML keeps its installed-schema requirements. |
| `hmi-languages` | Explicit language edits and new language columns. Native identity/context and source KEY protections remain in force. |
| `view-designer` | Existing `.hmi` property edits; all other exported assets are preserved. |

A transaction begins with a pinned source revision/hash and private content copy. Each successful stage advances its private version; the current workspace stays unchanged. Invalid stages leave the previous stage intact. No lock is held while a client is composing edits or reviewing a transaction.

Preview returns every staged request, the staged result hash and a review hash. Commit rechecks the transaction version, review hash, current workspace revision **and** current hash. This catches a workspace that changed and later returned to the same bytes. A conflict leaves the private stages intact and changes no workspace. Inspect/abort the transaction and begin a new one against the intended source; there is no silent rebase or automatic conflict resolution.

Commit publishes all stages as **one workspace revision**. Repeating the same commit version/review returns its durable receipt, even after later edits advance that workspace. An aborted transaction cannot be committed; a committed transaction cannot be aborted. Abort prevents publication and preserves the private records for inspection; it does not roll back another client's work.

Native Studio actions, controller reads/writes, SDK job sequences, external artifact saves and multi-workspace distributed commits cannot be placed inside a transaction. Export/save remains an explicit step after committing. All normal edit validation and native acceptance boundaries still apply.

## GUI

Open **Transactions…** from the main window. Select a saved workspace, then **Begin private transaction**. The editor accepts a typed stage JSON request using exact selectors/keys from the corresponding project or language browser. **Stage editor JSON** saves it privately; **Review staged changes** displays the complete requests and result hashes. **Commit reviewed transaction** publishes once, while **Abort private transaction** leaves the current workspace untouched.

The editor's `expectedVersion` must match the loaded transaction. Stage or explicitly reset an unsent editor draft before switching or closing. Saved stages survive closing the GUI and restarting the service. Refresh lists the latest 100 retained transactions and current workspace hashes. **Scheduling status** shows coordinator capacity and queues. After a commit, refresh the corresponding project window before making further edits or exporting.

## REST and MCP

| Endpoint | Request / effect |
| --- | --- |
| `GET /api/transactions?status=open&offset=0&limit=50` | Page summaries; limit 1..100. Status may be omitted. |
| `POST /api/transactions` | `workspaceId`, `workspaceType`, `expectedSha256`, optional `name`; creates a private baseline. |
| `GET /api/transactions/{id}` | Read the transaction, saved stage hashes and any commit receipt. Interrupted commits are reconciled under the workspace lease. |
| `POST /api/transactions/{id}/stage` | Append a private stage at `expectedVersion`. |
| `POST /api/transactions/{id}/preview` | `{ "expectedVersion": 1 }`; review current stages and report whether the base is still current. |
| `POST /api/transactions/{id}/commit` | `{ "expectedVersion": 1, "expectedReviewSha256": "<review hash>" }`; publish the reviewed result once. |
| `POST /api/transactions/{id}/abort` | `{ "expectedVersion": 1 }`; mark an open transaction aborted without changing its workspace. |

Example Logix stage combining scalar XML text and multilingual documentation:

```json
{
  "expectedVersion": 0,
  "xmlEdits": [
    { "operation": "set_text", "xPath": "//Program[@Name='Demo']/Description", "value": "Reviewed program description" }
  ],
  "documentationEdits": [
    { "ownerXPath": "//Controller/Tags/Tag[@Name='Record']", "kind": "description", "language": "es-ES", "text": "Registro de producción" }
  ]
}
```

Other stage families use `languageEdits` / `addLanguages`, or `viewDesignerEdits`. They use the same edit records as their ordinary shared-service APIs. Only the families appropriate to the selected workspace type are accepted. Unexpected top-level transaction request fields are rejected.

The `logix` extension exposes `studio_transactions`, `studio_transaction_get`, `studio_transaction_begin`, `studio_transaction_stage`, `studio_transaction_preview`, `studio_transaction_commit`, `studio_transaction_abort` and `studio_concurrency_status`. These generic workspace operations also cover the HMI workspace types above. Begin/stage/commit/abort use `studio_extensions_apply`; listing/get/preview/status use `studio_extensions_read`. Describe the operation to obtain its current schema/generation. Per-transaction calls take `{ "transactionId": "<id>", "request": <typed request> }`; get omits `request`, and begin takes `{ "request": <begin request> }`.

## Cancellation, recovery and limits

Cancelling a queued operation removes it before it runs. Once commit I/O begins it can finish despite a client disconnect; cancellation is not a rollback instruction. After a lost response, query the transaction and retry **the same commit version/review hash**. Do not generate a different transaction or blindly retry an ordinary native operation to resolve an unknown outcome.

Before publishing, the service records a commit intent containing the exact base/result and intended revision. On restart, and before another writer proceeds, it reconciles that intent. An unchanged base returns the transaction to open; the selected intended result finalizes its receipt. An unexpected head blocks writes with `transaction_recovery_required` and preserves evidence. Recovery never guesses a new result or executes native operations. A machine/disk failure can require manual inspection; hardware power-loss durability is not established by these checks.

Limits: 50 stages and 1000 total edits/new languages per transaction, at most 256 edits/new languages and 2 MiB JSON per stage, 4 MiB complete review, and 512 MiB total private stage storage. Edit-family limits can be lower. Up to 10000 transactions are retained locally; archive local state deliberately before that bound is reached. There is no automatic deletion of commit receipts or staged evidence. Data and receipts live under the service's ignored/local state directory, not in the public repository.

Validation includes deterministic lock/fairness/cancellation tests, racing commits, overlapping snapshot readers and writers, failed stages, ordinary-edit conflicts, durable retries after later revisions, simulated restart on either side of the manifest commit point, HMI identity preservation, View Designer asset preservation, GUI staging/review/commit/abort and an actual interprocess MCP overlap probe. These establish local service behavior, not native Studio or controller acceptance.
