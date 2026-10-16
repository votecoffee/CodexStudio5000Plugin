using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;

internal static class WatchIntegration
{
    public static async Task<string[]> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new Exception("FAIL: " + label); checks.Add(label); Console.WriteLine("PASS: " + label); }
        async Task<T> Tool<T>(string name, Dictionary<string, object?> args)
        {
            var result = await mcp.CallToolAsync(name, args);
            if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text);
            return JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        }
        var source = Path.Combine(root, "examples", "WatchDemo.L5X"); var hash = ProjectService.Hash(await File.ReadAllBytesAsync(source));
        var project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(source));
        var extraction = await Tool<StructureExtraction>("studio_structure_extract", new() { ["request"] = new StructureExtractRequest(ProjectId: project.Id, TagName: "Record") });
        Check(extraction.Origin == "saved_project" && extraction.Types.Length == 2 && extraction.Tags[0].Value!["Inner"]!["Count"]!.GetValue<int>() == 8,
            "MCP extracts nested UDT schemas and typed JSON from the shared project");
        var request = new WatchListRequest("Offline UDT integration", [new("record", "Record", "WatchProgram"), new("sample", "Record", "WatchProgram", "Samples[1]")],
            hash, SourcePath: source, Mode: "Offline", IntervalMs: 200, HistoryLimit: 3);
        var watch = await Tool<WatchStatus>("studio_watch_save", new() { ["request"] = request });
        try
        {
            Check(watch.State == "stopped" && watch.Latest is null, "MCP creates a stopped list without starting native polling");
            Check((await client.GetAsync<WatchStatus[]>("/api/watches")).Any(w => w.Definition.Id == watch.Definition.Id), "REST sees the same MCP-created watch list");
            watch = await client.PostAsync<WatchStatus>("/api/watches/" + watch.Definition.Id, new WatchUpdateRequest(request with { Name = "Updated offline UDT list" }, 0));
            Check((await Tool<WatchStatus>("studio_watch_get", new() { ["watchId"] = watch.Definition.Id })).Definition.Revision == 1,
                "MCP observes REST watch updates and revision control");
            var cap = await client.GetAsync<JsonElement>("/api/sdk/capabilities");
            if (cap.GetProperty("nativeBridgeConfigured").GetBoolean())
            {
                await Tool<WatchStatus>("studio_watch_start", new() { ["watchId"] = watch.Definition.Id });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                do
                {
                    await Task.Delay(500, timeout.Token);
                    watch = await client.GetAsync<WatchStatus>("/api/watches/" + watch.Definition.Id, timeout.Token);
                    if (watch.State == "faulted") throw new Exception("Native watch failed: " + watch.Error);
                } while (watch.Latest?.Sequence is not >= 3);
                Check(watch.Latest!.Values.All(v => v.Quality == "good") && watch.Latest.Values[0].Value!["Inner"]!["Count"]!.GetValue<int>() == 8 && watch.Latest.Values[1].Value!.GetValue<int>() == 20,
                    "Native persistent reader polls nested UDT and array-member values offline");
                var history = await Tool<WatchSnapshot[]>("studio_watch_history", new() { ["watchId"] = watch.Definition.Id });
                Check(history.Length == 3 && history.Zip(history.Skip(1)).All(p => p.First.CompletedAt <= p.Second.StartedAt),
                    "Shared polling history is bounded and read cycles do not overlap");
                watch = await Tool<WatchStatus>("studio_watch_stop", new() { ["watchId"] = watch.Definition.Id });
                Check(watch.State == "stopped" && watch.Stale, "Stopping through MCP closes the reader and marks cached samples stale");
            }
            Check(ProjectService.Hash(await File.ReadAllBytesAsync(source)) == hash, "Watch polling and native decoding preserve original source bytes");
            var reportDir = Path.Combine(root, "artifacts", "test-results"); Directory.CreateDirectory(reportDir);
            await Tool<JsonElement>("studio_watch_delete", new() { ["watchId"] = watch.Definition.Id });
            Check(!(await Tool<WatchStatus[]>("studio_watch_list", [])).Any(w => w.Definition.Id == watch.Definition.Id), "Deleting a stopped list removes it across both transports");
            await File.WriteAllTextAsync(Path.Combine(reportDir, "watches.json"), JsonSerializer.Serialize(new { checks, watch, extraction, controllerConnectionAttempted = false }, ProjectService.Json));
            return checks.ToArray();
        }
        finally
        {
            var current = await client.GetAsync<WatchStatus[]>("/api/watches");
            if (current.Any(w => w.Definition.Id == watch.Definition.Id))
            {
                await client.PostAsync<WatchStatus>("/api/watches/" + watch.Definition.Id + "/stop", new { });
                await client.PostAsync<JsonElement>("/api/watches/" + watch.Definition.Id + "/delete", new { });
            }
        }
    }
}
