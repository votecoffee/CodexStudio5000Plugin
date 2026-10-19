# Architecture

Version 0.7 adds `Studio5000.Extensibility` (stable contract), `Studio5000.ExtensionWorker` (one isolated process per selected/draining version), and `Studio5000.Extensions.Rockwell` (reloadable operation catalogs/compositions). The shared Core `ExtensionHost` validates packages, publishes generations and pins in-flight work. REST and the ten stable MCP tools use that host; the desktop generates operation fields from the same schemas. Existing endpoints/tools remain compatible. [EXTENSIONS.md](EXTENSIONS.md) describes lifecycle, jobs, trust and limits.

```text
Codex chat ── stdio MCP adapter ─┐
Windows GUI ── HTTP client ──────┼── authenticated loopback REST
Other API clients ──────────────┘                │
                                      shared ProjectService
                                      /         |         \
                              XML editing   L5xploder   NativeBridge
                                                |       local Logix SDK
                                      immutable revisions / artifacts
```

`Studio5000.Core` owns registration, inspection, edits, revision selection, source/output path policy, export, hashing and audit. `Studio5000.Service` hosts exactly one instance per state directory. `Studio5000.Client` is the common transport library. `Studio5000.Mcp` has no independent editing state. `Studio5000.Desktop` has only presentation state; its edits and saves go through the service. `Studio5000.NativeBridge` is a separately built process so the basic service does not require Rockwell packages.

## State

The service preserves source bytes as `projects/<id>/original.l5x` or `original.acd`. ACD registration exports that snapshot into L5X through the SDK. Each apply creates another immutable `revision-N.l5x`; an atomically replaced `project.json` selects the current revision. Registration and each operation record SHA-256 values. Draft files persist both the base/result hashes and the exact edit array. Apply recomputes the result, checks its hash, and serializes mutations through a semaphore. A file lock prevents a second service process from using the same state directory. A retry of an already-applied draft returns the matching current revision.

Exports are immutable managed artifacts with byte count, hash and validation label. API/GUI saves copy these to a **new** file under `OutputRoots`. Every stored artifact and working revision is rehashed when used. The source file may continue to change in Studio independently; this service intentionally operates on the snapshot registered at that time. Register again to capture a newly saved source.

Recomposition first snapshots an allowed exploded directory into a private staging tree. That snapshot is retained for investigation. Its recomposed L5X is the registered original and the `sourceSha256` refers to those recomposed L5X bytes. Explode/implode is a logical representation; ordering/formatting normalization means byte equality is not promised.

`audit.jsonl` records local operation events. It is an engineering audit trail, not a signed or tamper-proof compliance log. Failed registrations/conversions may leave partial managed staging data but never select it as a project or successful artifact. No automatic garbage collection is performed in v0.1.

## Local trust boundary

The service accepts only an HTTP loopback IP endpoint. Every endpoint except minimal health requires a random bearer token, held under the user's protected `.local` directory. Browser-origin requests are rejected. Host names are constrained. Source paths require configured roots, absolute names, and no symbolic links/junctions. XML DTD/external entities are prohibited. IDs are opaque GUIDs. There is no shell-command API; native execution uses a configured executable with discrete arguments. Native calls are serialized and time limited; cancellation kills the child process tree.

This is a single trusted Windows-user application. Another process running with that user's access can read its credentials or alter its files. It is not a multi-tenant or remote-access service. Public HTTPS hosting, OAuth and a secure tunnel would be separate work for a cloud API caller; `127.0.0.1` is reachable by local clients only. The REST API requires no OpenAI API key because it performs no inference calls.

## Editing limits

Supported edit operations are scalar `set_text` on Description/Text/Comment; `set_attribute` on an existing attribute within a Tag/Routine except Name/Number; `replace_element`; `add_element`; and `remove_element`. Components include tags, routines, rungs, ST lines, programs, UDTs and AOIs. One preview can contain up to 200 edits. Selectors must resolve to one element. Duplicate sibling names are rejected case-insensitively. RLL/ST content must match the routine type.

These are structural checks. References, rung semantics, safety signatures, I/O behavior, native compiler results, project version compatibility and commissioning remain separate. Source-protected and unsupported native content is preserved by snapshots but is not decoded by the XML editor. The separate SDK job API exposes the installed SDK's project, tag, controller, safety and SD card operations. Native availability means the bridge executable is configured; only successful execution establishes native evidence for that operation/project/version.

The native SDK runs under the current interactive Windows account because FactoryTalk authentication/licensing can depend on that account. The background process is deliberately a per-user shared service rather than an SCM service under LocalSystem.

## SDK jobs

`SdkSurface.json` freezes the installed 2.1.974 public API. Generated direct C# calls compile against the vendor package; a coverage command compares reflected signatures and inspects the bridge IL for bound method names. No arbitrary reflection or callback execution is exposed. Vendor logging callbacks and disposal are managed inside the child process.

Each job snapshots a hash-bound project/file/artifact and its imported XML files into `sdk-jobs/<id>/work`. File parameters are remapped to that directory. SaveAs cannot overwrite existing files. Native calls are serialized with conversions; each job retains one project session across its ordered calls, then disconnects and exports new ACD/L5X files. Success publishes hashed artifacts and a new managed project. The source project/revision remains preserved. The raw request is never persisted; password arguments travel over stdin and are redacted from collected diagnostics. Vendor logging outside this application has its own behavior.

REST returns a job ID immediately. MCP and GUI poll the same durable result. Cancellation kills the child process tree and is not rollback. If the process is interrupted after dispatch, its result is `indeterminate`; a service restart similarly marks unfinished jobs. Completed controller effects must be inspected before a retry. A terminal failure retains per-call results, diagnostics and staging without publishing it as a successful artifact. Output publication failures are distinct from prior SDK call completion and can leave retained files.

Controller entry points and `mode=Online` require both `EnableControllerOperations` and a selected configured `ControllerTargetId`. The communications path is injected from configuration, never accepted as an arbitrary request override. Setting a communications path on an offline copy requires a configured target but does not require enabling controller operations. SDK calls that go online hold that session only within the job; disposal ends it. Local offline fixture evidence does not establish live-controller, runtime, safety or commissioning acceptance.

## Watch sessions and structured reads

`ProjectService.Watches` owns persistent list definitions, source snapshots, scheduler tasks, cached samples and bounded history. REST/MCP/desktop are clients of that owner. Lists are created stopped and restore stopped after restart, with the persisted latest sample stale. Start checks configured controller access, method/program scope and exact source hashes. Poll cycles are serial; failures retain explicit quality/errors or fault the session without automatic reconnect. `IWatchSessionFactory` provides a deterministic reader seam for scheduler/scope/recovery tests.

The native bridge's `--watch` protocol opens a private source snapshot and a separate offline decoder project once per run. Only the source session goes online and only reads whole base tags. Raw bytes are applied to the offline decoder, which exports the selected tag as decorated XML. `LogixStructures` extracts nested objects/arrays from this representation without inferring byte offsets, padding or BOOL packing. Native operations share the conversion/job semaphore; idle watch sessions do not hold it. Pipe EOF ends the reader when the service exits. Stop/cancellation disposes or terminates the read-only native child.

The optional [CIP reader](CIP_READS.md) is a separate process with its own pinned dependencies. Core plans bounded symbolic scalar leaves against saved L5X metadata before handle initialization. The worker checks live primitive types, reads sequentially and reconstructs selected JSON structures. It has no controller write path. Both backends use the shared watch policy, scheduler, recording and stale/fault lifecycle. No backend fallback occurs after a failed read.

`ProjectService.Structures` extracts definitions/values from managed hash-checked revisions. A fresh live pull reuses the configured, gated SDK upload-to-new-project operation; extraction of that successful job is labeled an uploaded snapshot. The service does not describe ordinary saved data as live or assume a watch's saved schema updates after controller edits. Limits and API contracts are in [WATCHES.md](WATCHES.md).

## FactoryTalk ME/SE

`ProjectInfo.kind` distinguishes `logix`, `ftview-me` and `ftview-se`. Its default preserves older manifests. Graphics revisions use XML filenames while retaining the same project IDs, source snapshots, preview/apply locking, immutable artifacts and audit service. `FactoryTalkGraphics` handles local schema identity, XSD validation, existing-property edits, stable named selectors and changed-property comparison. FactoryTalk operations do not instantiate another service or own separate editing state.

The XSD reference is restricted to a local `Gfx-ME<n>.xsd` or `Gfx-SE<n>.xsd` filename in the configured schema directory. DTDs and external schema resolution are disabled. Registration can inspect structurally valid graphics when a matching schema is unavailable; property mutation/import preparation requires that schema. If an installed matching schema rejects an input, registration fails explicitly. Vendor XSD files are read from the installation and hashed, never modified or redistributed.

Property updates preserve element types, object/configuration identity, child structure and property names. Native `update` imports do not imply native object deletion or creation. Captions and expressions can change as values; names and `stateId`/`id` identities remain fixed. An import plan binds a working hash to explicit application/display/language targets and a fixed-display update batch entry. It stores original-export bytes, the edited XML, schema hash and property checks, plus separate native-backup and re-export locations.

The launcher/status API is bounded to the configured installed Studio executable and observes process/window metadata only. Native wizard actions belong to the bundled Codex skill using available Computer Use tools, or the user operating Studio. There is no undocumented COM server or guessed command-line import API. The service does not accept generic window-input scripts or claim to authenticate native origin from XML. Verification retains supplied export bytes and reports matching/mismatching values while preserving `nativeImportStatus=not_observed_by_service`.

## View Designer workspaces

View Designer uses the same ProjectService lock, path policy, hashing, artifacts and audit. Its dedicated `/api/viewdesigner` routes store `viewdesigner/<id>/original/`, `revision-N/` and an atomic project manifest. The tree hash includes sorted relative paths, per-file SHA-256 and byte lengths. The structural .hmi reader retains spans so property replacements preserve unrelated source bytes and assets. REST/MCP/desktop share all editing/import-package operations. Native VPD/VPDR, catalog validation and device runtime remain outside this implementation; see [VIEW_DESIGNER.md](VIEW_DESIGNER.md).
