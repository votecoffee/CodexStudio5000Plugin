using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class TransactionIntegration
{
    internal static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool valid, string text) { if (!valid) throw new Exception("FAIL: " + text); checks.Add(text); Console.WriteLine("PASS: " + text); }
        var catalog = await client.GetAsync<ExtensionCatalog>("/api/extensions"); var module = catalog.Extensions.Single(e => e.Id == "logix");
        async Task<CallToolResult> Call(string name, object arguments, bool write) => await mcp.CallToolAsync(write ? "studio_extensions_apply" : "studio_extensions_read", new Dictionary<string, object?> { ["call"] = new ExtensionCall("logix", name, module.Generation, JsonSerializer.SerializeToElement(arguments, ProjectService.Json)) });
        async Task<T> Tool<T>(string name, object arguments, bool write = false)
        { var reply = await Call(name, arguments, write); if (reply.IsError == true) throw new Exception(reply.Content.OfType<TextContentBlock>().First().Text); return JsonSerializer.Deserialize<T>(reply.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!; }
        var p = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(Path.Combine(root, "examples", "MultilingualDemo.L5X")));
        var a = await Tool<WorkspaceTransactionInfo>("studio_transaction_begin", new { request = new WorkspaceTransactionBeginRequest(p.Id, "project", p.CurrentSha256, "MCP writer") }, true);
        var b = await client.PostAsync<WorkspaceTransactionInfo>("/api/transactions", new WorkspaceTransactionBeginRequest(p.Id, "project", p.CurrentSha256, "REST writer"));
        a = await Tool<WorkspaceTransactionInfo>("studio_transaction_stage", new { transactionId = a.Id, request = new WorkspaceTransactionStageRequest(0, XmlEdits: [new("set_text", "//Program[@Name='Demo']/Description", "Writer A")]) }, true);
        b = await client.PostAsync<WorkspaceTransactionInfo>($"/api/transactions/{b.Id}/stage", new WorkspaceTransactionStageRequest(0, DocumentationEdits: [new("//Controller/Tags/Tag[@Name='Record']", "description", "es-ES", "Writer B")]));
        Check((await client.GetAsync<ProjectInfo>("/api/projects/" + p.Id)).CurrentSha256 == p.CurrentSha256 && a.Version == 1 && b.Version == 1, "Concurrent REST/MCP transactions retain private stages and leave the current workspace unchanged");
        var ap = await Tool<WorkspaceTransactionPreview>("studio_transaction_preview", new { transactionId = a.Id, request = new WorkspaceTransactionVersionRequest(a.Version) });
        var bp = await client.PostAsync<WorkspaceTransactionPreview>($"/api/transactions/{b.Id}/preview", new WorkspaceTransactionVersionRequest(b.Version));
        Check(ap.CanCommit && bp.CanCommit, "Both transactions review the same exact base before their competing commits");
        Check((await Call("studio_transaction_commit", new { transactionId = a.Id, request = new WorkspaceTransactionCommitRequest(a.Version, ap.ReviewSha256) }, false)).IsError == true, "Read-only MCP dispatch rejects transaction commit");
        var commitA = Call("studio_transaction_commit", new { transactionId = a.Id, request = new WorkspaceTransactionCommitRequest(a.Version, ap.ReviewSha256) }, true);
        async Task<bool> CommitB()
        { try { await client.PostAsync<WorkspaceTransactionInfo>($"/api/transactions/{b.Id}/commit", new WorkspaceTransactionCommitRequest(b.Version, bp.ReviewSha256)); return true; } catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { return false; } }
        var commitB = CommitB(); await Task.WhenAll(commitA, commitB);
        Check((commitA.Result.IsError == true ? 0 : 1) + (commitB.Result ? 1 : 0) == 1 && (await client.GetAsync<ProjectInfo>("/api/projects/" + p.Id)).Revision == 1, "Overlapping MCP/REST commits produce exactly one winner and a conflict, without a lost update");
        var winner = commitA.Result.IsError != true ? a : b; var review = winner.Id == a.Id ? ap.ReviewSha256 : bp.ReviewSha256;
        var before = await client.GetAsync<ProjectInfo>("/api/projects/" + p.Id); var draft = await client.PostAsync<ChangePreview>($"/api/projects/{p.Id}/previews", new PreviewRequest(before.CurrentSha256, [new("set_text", "//Program[@Name='Demo']/Description", "Later ordinary edit")]));
        await client.PostAsync<ProjectInfo>($"/api/projects/{p.Id}/previews/{draft.Id}/apply", new ApplyRequest(before.CurrentSha256));
        var retry = await Tool<WorkspaceTransactionInfo>("studio_transaction_commit", new { transactionId = winner.Id, request = new WorkspaceTransactionCommitRequest(winner.Version, review) }, true);
        Check(retry.Status == "committed" && retry.CommittedRevision == 1 && (await client.GetAsync<ProjectInfo>("/api/projects/" + p.Id)).Revision == 2, "Commit retry returns its durable receipt even after an ordinary edit advances the workspace");
        var loser = winner.Id == a.Id ? b : a;
        var conflict = await Tool<WorkspaceTransactionPreview>("studio_transaction_preview", new { transactionId = loser.Id, request = new WorkspaceTransactionVersionRequest(loser.Version) });
        var aborted = await Tool<WorkspaceTransactionInfo>("studio_transaction_abort", new { transactionId = loser.Id, request = new WorkspaceTransactionVersionRequest(loser.Version) }, true);
        Check(!conflict.CanCommit && aborted.Status == "aborted", "A conflicting private transaction can be inspected and explicitly aborted without rolling back another writer");
        var concurrency = await Tool<WorkspaceConcurrencyStatus>("studio_concurrency_status", new { });
        Check(concurrency.MaximumConcurrentMutations == 8 && concurrency.ActiveMutations == 0 && concurrency.QueuedMutations == 0 && ProjectService.Hash(File.ReadAllBytes(p.SourcePath)) == p.SourceSha256,
            "Shared scheduling returns to idle and preserves the original source after overlapping transactions");
        var fixture = catalog.Extensions.Single(e => e.Id == "fixture" && e.State == "active"); var barrier = Guid.NewGuid().ToString("N");
        async Task<JsonElement> Probe()
        {
            var result = await mcp.CallToolAsync("studio_extensions_apply", new Dictionary<string, object?> { ["call"] = new ExtensionCall("fixture", "fixture_concurrency_barrier", fixture.Generation, JsonSerializer.SerializeToElement(new { barrierId = barrier })) });
            if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text); return JsonSerializer.Deserialize<JsonElement>(result.Content.OfType<TextContentBlock>().First().Text);
        }
        var probes = await Task.WhenAll(Probe(), Probe());
        Check(probes.All(x => x.GetProperty("overlapped").GetBoolean()) && probes[0].GetProperty("processId").GetInt32() != probes[1].GetProperty("processId").GetInt32(), "One existing MCP connection executes overlapping operations in separate opted-in extension workers");
        return checks;
    }
}
