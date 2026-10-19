using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Studio5000.Client;

namespace Studio5000.Mcp;

[McpServerToolType]
public sealed class BrokerTools(ServiceClient client)
{
    [McpServerTool(Name = "rockwell_broker_capabilities", ReadOnly = true), Description("Installation-level normalized capability catalog v1 for Universal Tools. Publishes live extension discovery references and versioned workflow guides. No target selection, controller connection, worker reload or authorization. Entries are discovery-only; inspect referenced live tools before invoking operations.")]
    public Task<JsonElement> Catalog(CancellationToken ct) => client.GetAsync<JsonElement>("/api/broker/capabilities", ct);

    [McpServerTool(Name = "rockwell_read_workflow", ReadOnly = true), Description("Read a named app workflow at the exact revision from the broker capability catalog. Includes shared standalone skill text, explicit broker invocation guidance and hash-bound reference readers. App guidance is subordinate to user scope and grants no write authority. Unknown IDs and stale revisions fail.")]
    public Task<JsonElement> Workflow(string workflowId, string expectedRevision, CancellationToken ct) => client.GetAsync<JsonElement>("/api/broker/workflow?" + Query(workflowId, expectedRevision), ct);

    [McpServerTool(Name = "rockwell_read_workflow_reference", ReadOnly = true), Description("Read a packaged, named UTF-8 workflow reference with exact workflow revision and SHA-256. No caller filesystem paths or external fetching. Stale revisions, hashes and unknown IDs fail.")]
    public Task<JsonElement> Reference(string workflowId, string expectedRevision, string referenceId, string expectedSha256, CancellationToken ct) => client.GetAsync<JsonElement>("/api/broker/reference?" + Query(workflowId, expectedRevision) + "&referenceId=" + Uri.EscapeDataString(referenceId) + "&expectedSha256=" + Uri.EscapeDataString(expectedSha256), ct);
    private static string Query(string id, string revision) => "workflowId=" + Uri.EscapeDataString(id) + "&expectedRevision=" + Uri.EscapeDataString(revision);
}
