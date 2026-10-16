# Architecture

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

These are structural checks. References, rung semantics, safety signatures, I/O behavior, native compiler results, project version compatibility and commissioning remain separate. Source-protected and unsupported native content is preserved by snapshots but is not decoded by the editor. No online connection, mode change, force, tag write, controller download, or safety-signature API is exposed. Native availability means the bridge executable is configured; only a successful actual conversion demonstrates native operation for that project.

The native SDK runs under the current interactive Windows account because FactoryTalk authentication/licensing can depend on that account. The background process is deliberately a per-user shared service rather than an SCM service under LocalSystem.
