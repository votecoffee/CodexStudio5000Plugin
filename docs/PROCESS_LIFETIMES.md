# Process ownership and lifetimes

## Implemented source boundaries

The Windows .NET stdio adapter acquires one of a bounded set of installation-local file leases before initializing its MCP host. Exclusive open handles, rather than saved PIDs, represent active clients. The operating system releases handles after process exit; stale slot metadata is never authority to stop a process. Lowering the configured limit counts all held slots and never evicts another client. Admission inspects at most sixteen slots under one short-lived admission lock.

The adapter pins process handles for its direct parent and launcher wrappers (`powershell`, `pwsh`, `cmd`), up to four ancestors. Capture rejects a missing, exited or reused identity. An observed owner exit requests host shutdown. Stdio EOF uses the MCP SDK's normal host shutdown path. The configured host shutdown timeout bounds cooperative host shutdown; it is not a process-kill guarantee for a stuck native library.

Each frontend owns its admission lease and observation handles. It does not own the shared HTTP service, another frontend, GUI, watch or native job. Disconnecting does not cancel shared work or establish rollback. No process is terminated using a stale PID file or a process-name sweep.

The optional Python supervisor marks its own launched .NET service as supervised. That service observes the supervisor's pinned parent identity and requests shutdown on owner loss. Controlled supervisor exit and startup failure after launch close only the exact child represented by that invocation's `Popen` handle, with bounded termination/wait escalation. Reporting errors remain nonfatal. The directly launched service intentionally persists independently of its launching shell. The optional Python MCP frontend does not yet share .NET frontend admission/owner monitoring.

A `.local/maintenance-hold.json` marker blocks ordinary source launchers, installer/configurator mutations and the source .NET/Python server entry points. Its presence requires explicit operator release; no cleanup or reconnect path clears it automatically. Previously installed executables still need separate version verification.

## Owned service process tree

The Windows service source now enters an unnamed, non-inheritable-handle Job Object before business services or workers are initialized. The job uses `KILL_ON_JOB_CLOSE` and an active-process bound without breakaway flags. The owning service and ordinary children created from it share membership from creation; there is no post-launch PID sweep or assignment race. A containment setup failure refuses service startup. `MaximumOwnedProcesses` defaults to 32, accepts 1..128, includes the service and applies on its next start.

The sole job handle remains rooted for the entire service process lifetime. It is intentionally not disposed by the host/DI shutdown path: closing it while the service is still inside its own job would terminate the owner before cleanup/journaling completed. OS process exit closes that handle, providing the intended inherited-child cleanup boundary even after abrupt owner exit. Graceful worker disposal remains the first cleanup path. Abrupt termination does not make partial effects safe or certain.

Existing user-owned applications are never assigned to this job. A shared service keeps its own job independently of any frontend's lifetime. Normal child creation inherits Windows job membership, but WMI creation, external COM/service hosts and existing native applications may be outside it. See Microsoft's [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) and [job limit definitions](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information). Native launch/attachment behavior still requires specific acceptance.

New FactoryTalk Studio GUI launch through the service is blocked with `native_gui_ownership_required` until durable ownership and dirty-state recovery are integrated. The former fire-and-forget launcher did not retain that information. This prevents adding an unsaved native GUI to the service's kill-on-close tree without a recovery contract. Operators can launch Studio themselves and continue using file preparation/validation and read-only window status. Those applications are never claimed or terminated by this service.

Authenticated `/api/management/activity` adds `process_containment` with the selected profile, configured cap, OS-reported active count or explicit unavailability, unknown external native effects and `not-integrated` durable recovery. It exposes no process command lines, project names or cleanup action. This metadata does not authorize manager restart or substitute for a durable resource ledger.

## Remaining ownership work

Durable resource recovery and operating-system containment are not accepted runtime capabilities yet. Containment has a source implementation only. The pinned framework 0.18.0 package does not supply the forthcoming shared resource-ownership ledger. App integration must await a published, reviewed API rather than assuming package presence implements recovery.

The application must separately track exclusive launches, user-owned attachments and shared leases; pin process creation identities; retain dirty/uncertain evidence; and keep shared resources alive until their last authorized owner closes. Native SDK/COM/server-owned processes may fall outside a normal child tree. User-owned engineering applications must never be inferred to be disposable from their names or current project.

Existing native job receipts, extension indeterminate outcomes and restart barriers remain authoritative. A process exit is not proof that external engineering effects stopped or rolled back. No owner-loss path retries, reconciles or clears an uncertain operation.

## Validation

`McpClientLifetimeTests` exercises configuration bounds, file-handle admission, released/stale slots and lowering limits using generated temporary files. `OwnedProcessContainmentTests` checks policy, native ABI structure sizes, pre-entry metadata and rejected GUI launch; it never assigns the test runner to a job. `framework/tests/test_owned_child.py` exercises cleanup with in-memory child stubs only. None starts an engineering server or establishes real parent-loss cleanup.

Isolated runtime acceptance remains required for stdio EOF, parent and wrapper exit, abrupt frontend/supervisor death, simultaneous admission, startup failure, shutdown during a blocked operation and instance-count recovery. Later native acceptance must prove exclusive resource cleanup, preservation of user-owned attachments, shared last-owner behavior and durable dirty/uncertain evidence. These checks require an explicitly released runtime hold and disposable fixtures. Ordinary reconnect or installation is not acceptance.
