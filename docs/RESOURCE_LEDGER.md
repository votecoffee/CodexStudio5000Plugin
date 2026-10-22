# Native resource ledger contract

The shared Core now contains a .NET producer/reader for the reviewed framework 0.20 [resource record schema and ownership contract](https://github.com/votecoffee/CodexMCPBroker/blob/5b2cac50ca24d21a15b732e0424bb02fd794e1fb/docs/resource-ownership.md). It is a shared infrastructure component. Production service, worker and native-session integration is a separate step; this library alone does not make those paths migrated.

## Interoperability

`Core/Resources/ResourceRecord.cs` validates the wire model, explicit private/attached/shared ownership and report/terminate policies. Optional defaults are included before revision hashing. Process identities retain host fingerprint, PID, canonical decimal creation FILETIME and normalized image path. Identity or image uncertainty never becomes authority to terminate a replacement process.

`ResourceJson` implements the framework's compact, sorted, ASCII-escaped canonical JSON and Python-compatible finite float formatting. The revision is SHA-256 of the normalized model. Native/Python checks cover more than 1,000 timestamp values, including scientific notation, negative zero, Unicode labels and retained recovery attestations. Unknown fields, duplicate keys and invalid versions fail closed.

`ResourceLedger` uses the same byte-zero OS lock at `resources/ledger.lock`, flushed temporary files and atomic replacement. The private registry has explicit current-user/SYSTEM access. Defaults retain 32 active resources per app across generations, a 4,096-record history limit and 32 KiB per record. Malformed retained claims block admission; they are never silently skipped or deleted.

`WindowsResourceProcessDriver` captures host/PID/creation/image using one checked process handle. The host fingerprint uses the canonical MachineGuid from the 64-bit registry view. The default registry uses Local AppData without package redirection, matching the framework. Foreign/unverifiable hosts and live image mismatches remain unknown before process control. There is no process-name sweep or adoption by saved PID.

## Owner and cleanup policy

The actual surviving process creates its owner generation. Another generation or helper cannot impersonate it. A launch reservation must precede creation, and identity must be durable before execution. Atomic contained creation and stream preservation still need to be wired into production launchers; ordinary `Process.Start` followed by registration is not equivalent.

Attached/shared claims are report-only. An opaque native connection can have an explicit attachment claim without inventing an exclusive process PID. App-owned native adapters must still retain that connection's identity, real detach operation, last-owner leases and dirty/save/refusal policy. Releasing a metadata claim is not a substitute for those operations.

Cleanup checks current ownership, reviewed revision, other retained claims and recovery barriers, then flushes a dispatch intent before touching its held process/group handle. A lost result remains uncertain. Identical request IDs return the retained receipt; replacement cleanup is rejected. Explicit reconciliation preserves the original receipt/barrier and records the actor/evidence; no native write journal is cleared or replayed.

## Validation

`ResourceLedgerTests` uses synthetic process handles for policy, bounded admission, generation isolation, attachment/shared protection, durable idempotent cleanup and uncertain-result recovery. It never launches a native application.

The opt-in `framework/tests/test_dotnet_resources.py` suite runs only the harmless `tests/Studio5000.ResourceInterop` JSON/lock executable. It checks native/Python model revisions, cross-runtime updates, mutual OS-lock exclusion and exact identity/default-registry agreement for that helper. Use `ROCKWELL_DOTNET_RESOURCE_INTEROP=1` after building the helper. All records and lock files use explicit disposable fixture directories. This is not MCP, service, SDK, native application or controller acceptance.
