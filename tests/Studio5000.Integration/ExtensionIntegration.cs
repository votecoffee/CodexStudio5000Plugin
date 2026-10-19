using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class ExtensionIntegration
{
    public static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        async Task<T> Tool<T>(string name, Dictionary<string, object?> arguments)
        {
            var result = await mcp.CallToolAsync(name, arguments);
            if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text);
            return JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, ExtensionProtocol.Json)!;
        }
        var catalog = await Tool<ExtensionCatalog>("studio_extensions_catalog", new());
        Check(mcp.ServerInstructions?.Contains("studio_extensions_catalog", StringComparison.Ordinal) == true, "Stable MCP initialization teaches live discovery instead of caching extension instructions");
        if (catalog.Extensions.Length == 0) { checks.Add("Extension packages unavailable; reload integration skipped (use Verify-Isolated.ps1 for actual worker checks)."); return checks; }
        Check(catalog.LoadErrors.Length == 0, "All installed extension workers pass startup/catalog validation");
        var view = catalog.Extensions.Single(x => x.Id == "viewdesigner" && x.State == "active");
        var descriptor = await Tool<ExtensionOperation>("studio_extensions_describe", new()
        { ["extensionId"] = view.Id, ["operation"] = "viewdesigner_screen_summary", ["generation"] = view.Generation });
        Check(view.Operations.All(o => o.InputSchema.ValueKind == JsonValueKind.Undefined) && descriptor.InputSchema.GetProperty("properties").TryGetProperty("file", out _),
            "Compact live catalog omits schemas and describe returns the selected exact schema");
        Check(catalog.Extensions.Where(x => x.Id != "fixture").Sum(x => x.Operations.Length) == 58, "Live catalog exposes PLC where-used, FactoryTalk reference resolution, project comparison and ladder previews");
        var graphics = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(Path.Combine(root, "examples", "DemoME.xml")));
        var factoryTalk = catalog.Extensions.Single(x => x.Id == "factorytalk" && x.State == "active");
        var referenceCall = new ExtensionCall(factoryTalk.Id, "ftview_tag_references", factoryTalk.Generation,
            JsonSerializer.SerializeToElement(new { projectId = graphics.Id, request = new GraphicsReferenceRequest(graphics.CurrentSha256) }, ProjectService.Json));
        var references = await Tool<GraphicsReferenceReport>("studio_extensions_read", new() { ["call"] = referenceCall });
        Check(references.Total == 1 && references.References[0].Shortcut == "PLC" && references.References[0].TagPath == "Demo.CycleCount",
            "Stable MCP bridge discovers and invokes FactoryTalk references through the shared service");
        var restReferences = await client.PostAsync<GraphicsReferenceReport>($"/api/projects/{graphics.Id}/graphics-references", new GraphicsReferenceRequest(graphics.CurrentSha256, "missing"));
        Check(restReferences.Total == 0 && restReferences.Project.CurrentSha256 == graphics.CurrentSha256,
            "REST reference filtering preserves snapshot identity without connecting to a controller");
        var resolutionPath = Path.Combine(root, "examples", "ReferenceDemoME.xml");
        var resolutionDisplay = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(resolutionPath));
        var resolutionPlc = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(Path.Combine(root, "examples", "WatchDemo.L5X")));
        var resolutionRequest = new GraphicsResolveRequest(resolutionDisplay.CurrentSha256, [new("PLC", resolutionPlc.Id, resolutionPlc.CurrentSha256)],
            [new("/gfx[1]", "#1", "{[PLC]Program:WatchProgram.Record}")]);
        var resolutionCall = new ExtensionCall(factoryTalk.Id, "ftview_resolve_references", factoryTalk.Generation,
            JsonSerializer.SerializeToElement(new { projectId = resolutionDisplay.Id, request = resolutionRequest }, ProjectService.Json));
        var resolved = await Tool<GraphicsResolutionReport>("studio_extensions_read", new() { ["call"] = resolutionCall });
        Check(resolved.Total == 3 && resolved.References[0].DataType == "DINT" && resolved.References[1].DataType == "INT" && resolved.References[2].Status == "requires_shortcut_mapping",
            "Stable MCP read bridge resolves explicit parameter/shortcut context against saved PLC UDT and array metadata");
        var resolvedPage = await client.PostAsync<GraphicsResolutionReport>($"/api/projects/{resolutionDisplay.Id}/graphics-reference-resolution", resolutionRequest with { Query = "matched_in_snapshot", Offset = 1, Limit = 1 });
        Check(resolvedPage.Total == 2 && resolvedPage.References.Length == 1 && resolvedPage.References[0].ProjectSha256 == resolutionPlc.CurrentSha256 && resolvedPage.ParameterScopes.Contains("/gfx[1]"),
            "REST resolution filters statuses, pages occurrences and returns exact parameter scopes with bound snapshot hashes");
        var staleResolution = resolutionCall with { Arguments = JsonSerializer.SerializeToElement(new { projectId = resolutionDisplay.Id,
            request = resolutionRequest with { Shortcuts = [new("PLC", resolutionPlc.Id, "stale")] } }, ProjectService.Json) };
        Check((await mcp.CallToolAsync("studio_extensions_read", new Dictionary<string, object?> { ["call"] = staleResolution })).IsError == true,
            "Reference resolution rejects a stale PLC mapping through the MCP worker and shared service");
        var seDisplay = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(Path.Combine(root, "examples", "ReferenceDemoSE.xml")));
        var seResolved = await client.PostAsync<GraphicsResolutionReport>($"/api/projects/{seDisplay.Id}/graphics-reference-resolution", resolutionRequest with { ExpectedSha256 = seDisplay.CurrentSha256 });
        Check(seResolved.Project.Kind == "ftview-se" && seResolved.References.Select(r => r.Status).SequenceEqual(resolved.References.Select(r => r.Status)),
            "SE reference resolution shares the ME operation and preserves unresolved-context statuses");
        var reviewSource = Path.Combine(root, "examples", "ReviewDemo.L5X");
        var sourceHash = ProjectService.Hash(File.ReadAllBytes(reviewSource));
        var baseline = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(reviewSource));
        var candidate = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(reviewSource));
        var draft = await client.PostAsync<ChangePreview>($"/api/projects/{candidate.Id}/previews", new PreviewRequest(candidate.CurrentSha256,
            [new("set_text", "//Tag[@Name='Input']/Description", "Edited generated fixture"), new("set_text", "//Rung[@Number='1']/Text", "XIC(Enable)ADD(Input,2,Result);")]));
        candidate = await client.PostAsync<ProjectInfo>($"/api/projects/{candidate.Id}/previews/{draft.Id}/apply", new ApplyRequest(candidate.CurrentSha256));
        var logix = catalog.Extensions.Single(x => x.Id == "logix" && x.State == "active");
        var referencesPath = Path.Combine(root, "examples", "ReferencesDemo.L5X"); var referencesHash = ProjectService.Hash(File.ReadAllBytes(referencesPath));
        var referencesProject = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(referencesPath));
        var tagRequest = new TagReferenceRequest(referencesProject.CurrentSha256, "Input", "ExampleProgram");
        var tagCall = new ExtensionCall(logix.Id, "studio_tag_references", logix.Generation,
            JsonSerializer.SerializeToElement(new { projectId = referencesProject.Id, request = tagRequest }, ProjectService.Json));
        var usages = await Tool<TagReferenceReport>("studio_extensions_read", new() { ["call"] = tagCall });
        Check(usages.Total == 4 && usages.References.Count(r => r.Kind == "alias") == 1 && usages.References.Count(r => r.RoutineType == "ST") == 2,
            "Stable MCP where-used reports local RLL/ST occurrences and direct alias declarations while skipping comments");
        var globalUsages = await client.PostAsync<TagReferenceReport>($"/api/projects/{referencesProject.Id}/tag-references", tagRequest with { ProgramName = null, Limit = 1 });
        Check(globalUsages.Total == 2 && globalUsages.References.Length == 1 && globalUsages.References[0].ProgramName == "OtherProgram",
            "REST where-used honors local shadowing and pages controller-tag occurrences");
        var indexUsages = await client.PostAsync<TagReferenceReport>($"/api/projects/{referencesProject.Id}/tag-references", tagRequest with { TagName = "Index", ProgramName = null, Query = "index_expression" });
        Check(indexUsages.Total == 2 && indexUsages.References.All(r => r.Role == "index_expression"), "Shared scanner distinguishes tags used in indirect array indices");
        var staleTagCall = tagCall with { Arguments = JsonSerializer.SerializeToElement(new { projectId = referencesProject.Id, request = tagRequest with { ExpectedSha256 = "stale" } }, ProjectService.Json) };
        Check((await mcp.CallToolAsync("studio_extensions_read", new Dictionary<string, object?> { ["call"] = staleTagCall })).IsError == true && ProjectService.Hash(File.ReadAllBytes(referencesPath)) == referencesHash,
            "PLC where-used rejects stale hashes and preserves original source bytes through both transports");
        var compareRequest = new ProjectCompareRequest(baseline.Id, baseline.CurrentSha256, candidate.Id, candidate.CurrentSha256);
        var comparisonCall = new ExtensionCall(logix.Id, "studio_compare_projects", logix.Generation,
            JsonSerializer.SerializeToElement(new { request = compareRequest }, ProjectService.Json));
        var comparison = await Tool<ProjectComparisonReport>("studio_extensions_read", new() { ["call"] = comparisonCall });
        Check(comparison.TotalChanges == 2 && comparison.Modified == 2 && comparison.Differences.Any(d => d.Kind == "Rung"),
            "MCP structural comparison reports a tag edit and same-count rung change without parent duplicates");
        var filteredComparison = await client.PostAsync<ProjectComparisonReport>("/api/projects/compare", compareRequest with { Query = "Rung", Limit = 1 });
        Check(filteredComparison.TotalChanges == 2 && filteredComparison.FilteredChanges == 1 && filteredComparison.Differences.Length == 1,
            "REST comparison filters a paged report while retaining full change counts");
        var ladderRequest = new LadderPreviewRequest(baseline.CurrentSha256, "//Routine[@Name='MainRoutine']", Limit: 3);
        var ladderCall = new ExtensionCall(logix.Id, "studio_ladder_preview", logix.Generation,
            JsonSerializer.SerializeToElement(new { projectId = baseline.Id, request = ladderRequest }, ProjectService.Json));
        var ladder = await Tool<LadderPreviewReport>("studio_extensions_read", new() { ["call"] = ladderCall });
        Check(ladder.Total == 3 && ladder.Rungs.All(r => r.Status == "rendered" && r.Diagram!.Svg.Contains("<svg", StringComparison.Ordinal)) && ladder.Rungs[0].Diagram!.Nodes.Length == 5,
            "Stable MCP read dispatch returns branch geometry and passive SVG for generated ladder rungs");
        var staleLadder = ladderCall with { Arguments = JsonSerializer.SerializeToElement(new { projectId = baseline.Id, request = ladderRequest with { ExpectedSha256 = "stale" } }, ProjectService.Json) };
        var staleReply = await mcp.CallToolAsync("studio_extensions_read", new Dictionary<string, object?> { ["call"] = staleLadder });
        Check(staleReply.IsError == true && ProjectService.Hash(File.ReadAllBytes(reviewSource)) == sourceHash,
            "Ladder preview rejects stale snapshots and review operations preserve original inputs");
        var p = await client.PostAsync<ProjectInfo>("/api/viewdesigner/projects", new RegisterRequest(Path.Combine(root, "examples", "ViewDesignerDemo")));
        var call = new ExtensionCall("viewdesigner", "viewdesigner_screen_summary", view.Generation,
            JsonSerializer.SerializeToElement(new { projectId = p.Id, file = "User-Defined Screens/Overview.hmi" }));
        var summary = await Tool<JsonElement>("studio_extensions_read", new() { ["call"] = call });
        Check(summary.GetProperty("bindings").GetArrayLength() == 1 && summary.GetProperty("objects").GetInt32() > 1, "Stable MCP read tool executes new code from the extension worker");
        var rejected = await mcp.CallToolAsync("studio_extensions_read", new Dictionary<string, object?> { ["call"] = call with { Operation = "viewdesigner_apply_changes" } });
        Check(rejected.IsError == true, "Read-only dispatcher rejects a project mutation before invoking the worker");
        var fixture = catalog.Extensions.SingleOrDefault(x => x.Id == "fixture" && x.State == "active");
        if (fixture is null) return checks;
        // Fixture packages contain DIFFERENT compiled DLLs and operation sets, not just changed metadata.
        fixture = await Tool<ExtensionSummary>("studio_extensions_reload", new() { ["request"] = new ExtensionReloadRequest("fixture", "1", fixture.Generation) });
        var oldCall = new ExtensionCall("fixture", "fixture_echo", fixture.Generation, JsonSerializer.SerializeToElement(new { delayMs = 5000 }));
        var job = await Tool<ExtensionJob>("studio_extensions_start_job", new() { ["request"] = new ExtensionJobRequest("read", oldCall) });
        var second = await Tool<ExtensionSummary>("studio_extensions_reload", new() { ["request"] = new ExtensionReloadRequest("fixture", "2", fixture.Generation) });
        Check(second.Generation != fixture.Generation && second.Operations.Any(o => o.Name == "fixture_added_in_v2"), "Reload adds a new compiled operation without replacing the MCP client/process");
        catalog = await Tool<ExtensionCatalog>("studio_extensions_catalog", new());
        Check(catalog.Extensions.Any(x => x.Generation == fixture.Generation && x.State == "draining" && x.ActiveCalls == 1), "Old extension worker stays pinned while its job runs");
        var newCall = new ExtensionCall("fixture", "fixture_added_in_v2", second.Generation, JsonSerializer.SerializeToElement(new { }));
        var newer = await Tool<JsonElement>("studio_extensions_read", new() { ["call"] = newCall });
        Check(newer.GetProperty("version").GetString() == "2", "Existing MCP connection discovers and executes the new operation");
        var large = await Tool<JsonElement>("studio_extensions_read", new() { ["call"] = newCall with { Operation = "fixture_large_result" } });
        Check(large.GetProperty("payload").GetString()!.Length == 3 * 1024 * 1024, "Recording-sized results over 2 MiB cross the worker, shared service and fixed MCP bridge intact");
        rejected = await mcp.CallToolAsync("studio_extensions_read", new Dictionary<string, object?> { ["call"] = oldCall });
        Check(rejected.IsError == true, "Stale generation is rejected instead of silently running a different version");
        for (var i = 0; i < 200 && job.Status is "queued" or "running"; i++)
        { await Task.Delay(50); job = await client.GetAsync<ExtensionJob>("/api/extensions/jobs/" + job.Id); }
        Check(job.Status == "succeeded" && job.Result!.Value.GetProperty("version").GetString() == "1", "In-flight job completes on the original compiled extension version");
        Check(job.Result!.Value.GetProperty("processId").GetInt32() != newer.GetProperty("processId").GetInt32(), "Extension versions run in separate worker processes");
        var bad = Path.Combine(root, "extensions", "fixture", "versions", "bad"); Directory.CreateDirectory(bad);
        await File.WriteAllTextAsync(Path.Combine(bad, "manifest.json"), "{\"id\":\"fixture\",\"version\":\"bad\",\"contractVersion\":999}");
        rejected = await mcp.CallToolAsync("studio_extensions_reload", new Dictionary<string, object?> { ["request"] = new ExtensionReloadRequest("fixture", "bad", second.Generation) });
        Check(rejected.IsError == true && (await client.GetAsync<ExtensionCatalog>("/api/extensions")).Extensions.Any(x => x.Generation == second.Generation && x.State == "active"), "Rejected reload retains the working generation");
        var rollback = await Tool<ExtensionSummary>("studio_extensions_reload", new() { ["request"] = new ExtensionReloadRequest("fixture", "1", second.Generation) });
        Check(!rollback.Operations.Any(o => o.Name == "fixture_added_in_v2"), "Explicit rollback restores the older operation catalog");
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "test-results", "extensions.json"), JsonSerializer.Serialize(new { checks, oldJob = job, newResult = newer, rollback, controllerOperations = "disabled; no controller requests" }, ProjectService.Json));
        return checks;
    }
}
