# Bounded MCP frontdoor profiles

The optional immutable `frontdoor.json` payload separates stateless chat connections from engineering worker admission. The stable app-owned entry point still selects a complete, hash-bound frontend. A selected profile runs the SDK in the actual `Studio5000.StdioHost` process; it does not create another MCP adapter child. Without this payload, the existing paired-host profile remains available.

## Admission and ownership

Metadata connections share the app's OS-handle slot directory with legacy connections. The explicit policy caps the combined count; it does not evict a client when a bound is lowered. A separate startup ramp limits concurrent SDK construction. Legacy `MaximumMcpClients` behavior remains unchanged for the legacy profile.

The actual frontdoor process owns a typed, processless, attached/report-only `mcp-frontdoor` observation in the common resource ledger under the fixed app-owned component ID `rockwell-mcp-frontdoor`. Its owner field contains the full genuine process generation. It owns admission handles and protocol streams, grants no process-termination authority, and cannot launch native work through this observation. Native workers, engineering jobs and their uncertainty remain under the main application's unchanged resource and operation limits.

Normal EOF closes admission handles before the exact current actor publishes its revision-bound terminal detach attestation. After a crash, only a positively departed full owner generation with the exact metadata role, reviewed executable path and no receipt/barrier/conflicting identity can be reconciled. Unknown/native/shared/changed observations remain protected. A new version needs a fully pinned predecessor frontdoor policy before cross-version metadata reconciliation.

The real SDK host observes its launcher/parent handles. Its lifetime may ensure the pinned ordinary offline service through the existing guarded Core operation. It is outside a private MCP child job; a service started this way keeps its own genuine owner and workers after frontend EOF. This grants no Windows SDK-service, controller, native bridge or HMI launch authority.

## Request and memory bounds

Initialize and tool listing establish transport availability. Engineering capability comes from the current genuine service catalog. A connection does not reserve an engineering worker for its entire lifetime.

Each new frontdoor has a bounded call window. Catalog/guidance reads do not take the temporary forwarding slots used by other tools. Those slots bound forwarding from the new profile; they do not claim to impose a new universal job cap on legacy clients or the GUI. Service-side worker/job admission, project revisions, target policies and mutation queues still apply.

The profile bounds incoming newline-delimited JSON frame bytes and buffered HTTP response bytes. It refuses new admission under available-commit pressure and refuses further forwarding when its own private commit exceeds the reviewed allowance. Size or capacity refusal never automatically retries an engineering request or terminates a peer. Large engineering artifacts should use the app's artifact/file workflow rather than an oversized inline result.

Commit reservations are admission models, not OS memory limits or permission to reclaim a user's software. Review measured normal and reconnect demand, launcher/SDK/service process counts, private commit and available system commit. Include legacy overlap and temporary startup processes. Verify the actual candidate's footprint in isolated fixtures before selecting a larger policy.

## Prepare a policy

Generate into a fresh frontend payload directory after publishing the MCP/stdio closure:

```powershell
.\scripts\New-McpFrontdoorPolicy.ps1 -Destination C:\Prepared\Mcp\frontdoor.json -MaximumMetadataClients 16 -MaximumStartingClients 2 -MaximumForwardedCalls 4
```

The producer defaults to eight metadata connections, a startup ramp of two and four forwarding slots. Its reservation includes a launcher allowance, a 128-MiB SDK-process admission allowance, a 2-MiB input frame limit and an 8-MiB buffered response limit. It never edits service settings, selects a pointer, starts a backend or overwrites an existing policy.

The profile is part of the frontend inventory and changes with reviewed frontend selection. Full app builds generate the conservative default. Frontend-only updates can pin an existing accepted service and use an independently reviewed demand-based policy. Existing services, clients and native documents remain separate ownership scopes. Preparing a profile or passing an isolated fixture is not installed/chat/native acceptance.
