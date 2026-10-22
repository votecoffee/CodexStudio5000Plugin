using System.Text.Json;

namespace Studio5000.Core;

// Static implementation/assessment metadata only. Reading this never starts a job or selects a target.
public static class ProtocolFeatureReport
{
    public static JsonElement Read() => JsonSerializer.SerializeToElement(new
    {
        schema_version = 1,
        profile = "standalone-dotnet",
        installed_route_acceptance = "unknown: inspect the connected client's tool catalog and running service version",
        features = new[]
        {
            Feature("current-guidance", "enabled-and-tested", "Pinned workflow/reference readers use shared Core canonical sources; packaged skills are small bootstraps."),
            Feature("frontend-lifetimes", "implemented-but-unassessed", "Windows frontend ownership follows pinned process handles through launcher wrappers. Configurable OS-owned file slots default to four clients; existing clients are not evicted. Graceful shutdown requests do not establish native rollback. Real process-loss/EOF/kill validation and installed acceptance remain outstanding."),
            Feature("skills-over-mcp", "blocked", "This .NET endpoint configures no Skills handlers. Explicit optional Python --skills profile provides list/get/resource reads and atomic metadata publication; installation does not select it."),
            Feature("catalog-notifications", "blocked", "No tool/resource change event feed is configured on this endpoint. Extension generations require explicit catalog rediscovery."),
            Feature("discovery-cache", "blocked", "The optional downstream publication remains transient/legacy with watch_catalog=false, TTL=0 and no assessed cache hints. Retained ownership must be assessed before changing this profile."),
            Feature("progress", "blocked", "SDK/extension jobs expose structured status; no native job-to-MCP-request progress relay is configured. No estimated percentages are emitted."),
            Feature("cancellation", "implemented-but-unassessed", "Ordinary job cancel/status tools remain available; MCP request cancellation is not bound to the durable job. An acknowledgment or disconnect does not establish rollback or native stop."),
            Feature("tasks", "blocked", "Existing SDK/extension jobs lack a trusted per-frontend task owner and an atomic durable task/start-intent correlation exposed by the shared service. No TaskExtension routes are advertised or generic calls wrapped."),
            Feature("approval-continuations", "blocked", "Existing app review tokens remain authoritative. No app-owned modern elicitation resolver binds a human answer to the exact target/review revision."),
            Feature("artifact-resources", "blocked", "Ordinary authenticated artifact readers remain available; no owner-bound bounded MCP resource adapter or authorized update feed is configured."),
            Feature("tray-reporting", "implemented-but-unassessed", "Reporting-only ManagedService integration is separately enabled by the operator; package presence does not establish a running registration."),
            Feature("tray-restart", "blocked", "No atomic fence covers all chats, unsaved GUI documents, prepared reviews and unresolved native effects. Managed restart is disabled."),
            Feature("native-worker-owner-loss", "blocked", "Controlled supervisor cleanup and supervised-service owner observation are implemented. Abrupt native worker/process-tree loss is not assessed; process exit does not prove that an external SDK operation stopped or rolled back."),
            Feature("remote-oauth-redis-apps", "outside-workflow", "Remote deployments, replicas and MCP Apps remain optional/deferred. One shared service owns native engineering state.")
        },
        evidence_boundary = "Synthetic source/package/protocol checks do not establish installed Codex skill activation, native application or controller acceptance. Notifications are hints; refetch after reconnect. Never retry an uncertain effect."
    });

    private static object Feature(string id, string state, string reason) => new { id, state, reason };
}
