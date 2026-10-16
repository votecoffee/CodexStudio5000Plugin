using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class BrowserRecordingIntegration
{
    public static async Task<string[]> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new Exception("FAIL: " + label); checks.Add(label); Console.WriteLine("PASS: " + label); }
        var catalog = await client.GetAsync<ExtensionCatalog>("/api/extensions"); var module = catalog.Extensions.Single(e => e.Id == "watches" && e.State == "active");
        async Task<T> Call<T>(string operation, string effect, object request, string? id = null)
        {
            var args = new Dictionary<string, object?> { ["request"] = request }; if (id is not null) args.Add("watchId", id);
            var call = new ExtensionCall("watches", operation, module.Generation, JsonSerializer.SerializeToElement(args, ProjectService.Json));
            var result = await mcp.CallToolAsync("studio_extensions_" + effect, new Dictionary<string, object?> { ["call"] = call });
            if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text);
            return JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        }
        var source = Path.Combine(root, "examples", "WatchDemo.L5X"); var hash = ProjectService.Hash(await File.ReadAllBytesAsync(source));
        var project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(source));
        var browser = await Call<TagBrowserPage>("studio_tag_browse", "read", new TagBrowseRequest(project.Id, Query: "Record"));
        Check(browser.Nodes.Length == 2 && browser.Types.Length == 2 && browser.Programs.Contains("WatchProgram"), "Stable MCP dispatch searches tags by scope/type without connecting");
        var children = await Call<TagBrowserPage>("studio_tag_browse", "read", new TagBrowseRequest(project.Id, browser.SourceSha256, "WatchProgram", "Record", "Samples", Offset: 1, Limit: 1));
        Check(children.Total == 3 && children.Nodes.Single().MemberPath == "Samples[1]", "Lazy browser pages exact array member paths through the extension worker");
        var watch = await Call<WatchStatus>("studio_tag_create_watch", "apply", new BrowserWatchRequest(project.Id, browser.SourceSha256, "Browser integration", [new("sample", "Record", "WatchProgram", "Samples[1]")]));
        var id = watch.Definition.Id;
        try
        {
            Check(watch.State == "stopped" && watch.Definition.Settings.SourceArtifactId is not null && watch.Latest is null, "Browser creates a stopped hash-bound watch with an immutable source artifact");
            var paused = await Call<WatchRecordingStatus>("studio_watch_recording_configure", "apply", new WatchRecordingUpdate(new(false, 2, 8), 0), id);
            var resumed = await client.PostAsync<WatchRecordingStatus>($"/api/watches/{id}/recording", new WatchRecordingUpdate(new(true, 2, 8), paused.Revision));
            Check(paused.Settings.Enabled == false && resumed.Revision == 2, "MCP and REST share recording settings with optimistic concurrency");
            var history = await Call<WatchRecordingPage>("studio_watch_recording_query", "read", new WatchRecordingQuery(Limit: 1), id);
            Check(history.HasMore && history.Records.Single().Kind == "recording_disabled" && history.Stale && history.Definition?.Id == id, "Recording query returns durable gap events, source definition and explicit stale state");
            var next = await client.PostAsync<WatchRecordingPage>($"/api/watches/{id}/recording/query", new WatchRecordingQuery(AfterIndex: history.NextIndex));
            Check(next.Records.Single().Kind == "recording_enabled", "Recording cursor advances without repeating events");
            var artifact = await Call<Artifact>("studio_watch_recording_export", "apply", new WatchRecordingQuery(Limit: 1), id);
            var data = await client.DownloadAsync(artifact.Id); var csv = System.Text.Encoding.UTF8.GetString(data);
            Check(artifact.Format == "csv" && ProjectService.Hash(data) == artifact.Sha256 && csv.Contains("recording_disabled") && csv.Contains("recording_enabled"), "CSV artifact retains all selected events beyond display pagination and has a verified hash");
            var saved = await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(Path.Combine(root, "artifacts", "exports", "watch-" + id + ".csv")));
            Check(File.Exists(saved.Path) && saved.Sha256 == artifact.Sha256 && ProjectService.Hash(await File.ReadAllBytesAsync(source)) == hash, "Shared artifact save exports recording CSV and preserves original PLC source");
        }
        finally { await client.PostAsync<JsonElement>($"/api/watches/{id}/delete", new { }); }
        return checks.ToArray();
    }
}
