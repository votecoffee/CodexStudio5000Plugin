# Native resource ledger contract

The shared Core now contains a .NET producer/reader for the reviewed framework 0.20 [resource record schema and ownership contract](https://github.com/votecoffee/CodexMCPBroker/blob/5b2cac50ca24d21a15b732e0424bb02fd794e1fb/docs/resource-ownership.md). It is a shared infrastructure component. Production service, worker and native-session integration is a separate step; this library alone does not make those paths migrated.

## Interoperability

`Core/Resources/ResourceRecord.cs` validates the wire model, explicit private/attached/shared ownership and report/terminate policies. Optional defaults are included before revision hashing. Process identities retain host fingerprint, PID, canonical decimal creation FILETIME and normalized image path. Identity or image uncertainty never becomes authority to terminate a replacement process.

`ResourceJson` implements the framework's compact, sorted, ASCII-escaped canonical JSON and Python-compatible finite float formatting. The revision is SHA-256 of the normalized model. Native/Python checks cover more than 1,000 timestamp values, including scientific notation, negative zero, Unicode labels and retained recovery attestations. Unknown fields, duplicate keys and invalid versions fail closed.

`ResourceLedger` uses the same byte-zero OS lock at `resources/ledger.lock`, flushed temporary files and atomic replacement. The private registry has explicit current-user/SYSTEM access. Defaults retain 32 active resources per app across generations, a 4,096-record history limit and 32 KiB per record. Malformed retained claims block admission; they are never silently skipped or deleted.

`WindowsResourceProcessDriver` captures host/PID/creation/image using one checked process handle. The host fingerprint uses the canonical MachineGuid from the 64-bit registry view. The default registry uses Local AppData without package redirection, matching the framework. Foreign/unverifiable hosts and live image mismatches remain unknown before process control. There is no process-name sweep or adoption by saved PID.

## Owner and cleanup policy

The actual surviving process creates its owner generation. Another generation or helper cannot impersonate it. A launch reservation must precede creation, and identity must be durable before execution. Atomic contained creation and stream preservation still need to be wired into production launchers; ordinary `Process.Start` followed by registration is not equivalent.

`WindowsChildProcess` now provides the atomic creation component for that wiring. It creates the child suspended with both the job-list and explicit stdio-handle-list attributes, binds identity durably, then resumes. Only the child ends of stdin/stdout/stderr are inheritable; the job handle and parent pipe ends are not. Raw byte streams remain available for protocol transport. Failed pre-resume startup closes the owned job without executing child code. Membership capture checks the exact job through the same process handle, so a reused PID is not adopted from an old snapshot. See Microsoft's [process/thread attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute) and [redirected stream rules](https://learn.microsoft.com/en-us/windows/win32/procthread/creating-a-child-process-with-redirected-input-and-output).

Live job handles remain rooted by the owning process. A normal dirty/recovery refusal keeps the process alive instead of letting garbage collection turn refusal into kill-on-close. Explicit successful cleanup closes all streams/handles and removes the retained root. Abrupt owner exit still closes the private kernel job; recovery records remain. These components do not replace native save/close/refusal or shared-lease adapters.

Attached/shared claims are report-only. An opaque native connection can have an explicit attachment claim without inventing an exclusive process PID. App-owned native adapters must still retain that connection's identity, real detach operation, last-owner leases and dirty/save/refusal policy. Releasing a metadata claim is not a substitute for those operations.

Cleanup checks current ownership, reviewed revision, other retained claims and recovery barriers, then flushes a dispatch intent before touching its held process/group handle. A lost result remains uncertain. Identical request IDs return the retained receipt; replacement cleanup is rejected. Explicit reconciliation preserves the original receipt/barrier and records the actor/evidence; no native write journal is cleared or replayed.

## Validation

`ResourceLedgerTests` uses synthetic process handles for policy, bounded admission, generation isolation, attachment/shared protection, durable idempotent cleanup and uncertain-result recovery. It never launches a native application.

The opt-in `framework/tests/test_dotnet_resources.py` suite runs only the harmless `tests/Studio5000.ResourceInterop` JSON/lock executable. It checks native/Python model revisions, cross-runtime updates, mutual OS-lock exclusion and exact identity/default-registry agreement for that helper. Use `ROCKWELL_DOTNET_RESOURCE_INTEROP=1` after building the helper. All records and lock files use explicit disposable fixture directories. This is not MCP, service, SDK, native application or controller acceptance.

With `ROCKWELL_DOTNET_RESOURCE_PROCESSES=1`, `test_dotnet_launch.py` exercises only disposable Python children through the compiled helper: binary stdio/EOF fidelity, descendant recording and cleanup, pre-resume failure, refusal until explicit harmless evidence, and nested-job owner crash. No app/MCP/native engineering host is started. `OwnedChildPolicyTests` checks command boundaries, bounds and native structure sizes without launching anything. Actual SDK stdio/native behavior still needs its own acceptance.

## Service workers and operator stop

The service creates its own `ResourceSupervisor` after containment and before worker initialization. Extension and read-only CIP worker launches use the atomic native launcher and the service's actual owner generation. Direct startup publishes a full creation-bound service identity; managed startup passes the same explicit registry to both runtimes.

`Stop-Service.ps1` sends a signed private request to the exact surviving owner. The direct/legacy path requires the current host fingerprint, held process handle, image and creation time; an old PID file alone is insufficient. Neither branch terminates a process by PID. The service fences project mutations, polling, native jobs and extension calls before closing owned children. A refusal retains the service and its handles. A dispatched close with no known result is retained as uncertain. Stop intent history uses an exclusive file claim so reconstructing a mailbox or delivering concurrent requests cannot replay a close.

The Python supervisor forwards the request to the matching .NET service owner and waits for actual service exit. Ctrl+C requests the same app-owned close rather than unwinding the owning launch context. After resolving a verified zero-effect refusal, an operator can explicitly use `Stop-Service.ps1 -ReviewBlockedStop` for a new review. This does not override uncertain outcomes or native recovery barriers. File/stub tests cover both stop branches in Windows PowerShell and PowerShell 7; these are not actual service shutdown acceptance.

## Native sessions and shared connections

`NativeSessionRegistry` records explicit provider/instance identities for SDK, COM, WMI and native-process attachments. A provider session correlation identifies a connection; it does not assert an exclusive vendor engine PID. A process attachment requires the full host/creation/image identity and is checked again during binding. App-created SDK/conversion/watch workers now use the atomic private launcher with a separate report-only native-session claim. The worker's interrupt/exit cannot clear that native claim.

Each connection has bounded local reference counts. Intermediate lease release retains the connection; only the last local reference invokes the app-owned native close adapter. Last-owner status never grants permission to terminate shared or user-owned software. The adapter must attest saved or explicitly discardable private documents, known operation effects, and connection close. Dirty refusal retains the connection and requires explicit review before another close. A lost close result remains uncertain across the resource/session files and cannot be retried or converted into a refusal.

The SDK bridge journals an exact worker/session handshake before native entry and checkpoints each successful call without arguments, secrets or controller addresses. It saves final working copies before disconnect/dispose completion. On failure it attempts a separate private recovery ACD while retaining the failed call's unknown effects. Conversion records close only after native disposal and a nonempty output. Watch sessions detach from their read-only source and explicitly dispose their private decoding mirror. The service requires the exact closed receipt and successful worker exit; missing, changed or partial evidence leaves a recovery barrier. These receipts describe native connection/document lifecycle, not controller acceptance or rollback.

An inner service job can retire its own worker despite the retained outer supervisor snapshot only when that snapshot names the exact current service process as its private Windows-job root. Attachments, siblings, other apps, receipt-bearing or departed generations still block cleanup. The common manager remains conservative about these overlapping claims and cannot apply this service-only delegation.

Native session/lease fixtures use synthetic providers and identities. The proprietary SDK bridge has been compiled with the new lifecycle protocol, but native operations have not been run for this increment. Native GUI lifecycle and optional framework stdio ownership remain separate source work.
