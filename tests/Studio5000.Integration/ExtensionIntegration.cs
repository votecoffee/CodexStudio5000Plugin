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
        Check(catalog.Extensions.Where(x => x.Id != "fixture").Sum(x => x.Operations.Length) == 142, "Live catalog exposes HMI languages, multilingual documentation, diagnostic tracing/evaluation/triggers, saved trend results, three-source merging, compatibility workflows and existing engineering tools");
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
        var watches = catalog.Extensions.Single(x => x.Id == "watches" && x.State == "active");
        var cipRequest = new CipWatchPlanRequest(resolutionPlc.CurrentSha256, [new("record", "Record", "WatchProgram"), new("count", "Record", "WatchProgram", "Inner.Count")]);
        var cipCall = new ExtensionCall(watches.Id, "studio_cip_watch_plan", watches.Generation,
            JsonSerializer.SerializeToElement(new { projectId = resolutionPlc.Id, request = cipRequest }, ProjectService.Json));
        var cipPlan = await Tool<CipWatchPlanReport>("studio_extensions_read", new() { ["call"] = cipCall });
        Check(cipPlan.UniqueLeafReads == 6 && cipPlan.Selections[1].Value.Address == "Program:WatchProgram.Record.Inner.Count",
            "Stable MCP read operation plans symbolic UDT leaves without a configured reader or controller");
        var restCipPlan = await client.PostAsync<CipWatchPlanReport>($"/api/projects/{resolutionPlc.Id}/cip-watch-plan", cipRequest);
        Check(JsonSerializer.Serialize(restCipPlan) == JsonSerializer.Serialize(cipPlan) && restCipPlan.SourceSha256 == resolutionPlc.CurrentSha256,
            "REST and MCP use the same hash-bound symbolic plan");
        var staleCipCall = cipCall with { Arguments = JsonSerializer.SerializeToElement(new { projectId = resolutionPlc.Id, request = cipRequest with { ExpectedSha256 = "stale" } }, ProjectService.Json) };
        Check((await mcp.CallToolAsync("studio_extensions_read", new Dictionary<string, object?> { ["call"] = staleCipCall })).IsError == true,
            "Symbolic read planning rejects stale source hashes through the extension worker");
        var preflightSource = Path.Combine(root, "examples", "WatchDemo.L5X");
        var preflightHash = ProjectService.Hash(await File.ReadAllBytesAsync(preflightSource));
        var preflightDraft = new WatchListRequest("Preflight integration", [new("ok", "Record", "WatchProgram"), new("bad", "Message", "WatchProgram")],
            preflightHash, SourcePath: preflightSource, ControllerTargetId: "unconfigured", Backend: "LibPlcTag");
        var preflightCall = new ExtensionCall(watches.Id, "studio_watch_preflight", watches.Generation,
            JsonSerializer.SerializeToElement(new { request = new WatchPreflightRequest(Settings: preflightDraft) }, ProjectService.Json));
        var beforeWatches = await client.GetAsync<WatchStatus[]>("/api/watches");
        var preflight = await Tool<WatchPreflightReport>("studio_extensions_read", new() { ["call"] = preflightCall });
        Check(preflight.Status == "blocked" && preflight.Checks.Any(c => c.Issue?.Code == "sdk_target_unknown") &&
            preflight.Selections[0].Status == "supported" && preflight.Selections[1].Issues[0].Code == "cip_type_unsupported" && preflight.Budget is { Complete: false, UniqueSupportedLeafReads: 6 },
            "MCP preflight reports all selection diagnostics and an explicitly partial budget with controller access disabled");
        var restPreflight = await client.PostAsync<WatchPreflightReport>("/api/watches/preflight", new WatchPreflightRequest(Settings: preflightDraft));
        Check(JsonSerializer.Serialize(restPreflight) == JsonSerializer.Serialize(preflight), "REST and MCP return the same read-only watch preflight");
        var browserPreflight = await client.PostAsync<WatchPreflightReport>("/api/watches/preflight", new WatchPreflightRequest(Browser:
            new(resolutionPlc.Id, resolutionPlc.CurrentSha256, "Browser preflight", [new("count", "Record", "WatchProgram", "Inner.Count")])));
        Check(browserPreflight.SourceHashMatches && browserPreflight.Selections.Single() is { Status: "supported", DataType: "DINT" } &&
            (await client.GetAsync<WatchStatus[]>("/api/watches")).Length == beforeWatches.Length && ProjectService.Hash(await File.ReadAllBytesAsync(preflightSource)) == preflightHash,
            "Browser preflight inspects a managed snapshot without creating a list or modifying the source");
        var preflightSaved = await client.PostAsync<WatchStatus>("/api/watches", preflightDraft with { Backend = "Sdk", Mode = "Offline", ControllerTargetId = null });
        try
        {
            var savedPreflight = await client.GetAsync<WatchPreflightReport>($"/api/watches/{preflightSaved.Definition.Id}/preflight");
            Check(savedPreflight.WatchId == preflightSaved.Definition.Id && savedPreflight.WatchRevision == 0 && savedPreflight.Selections.All(s => s.Status == "supported") &&
                (await client.GetAsync<WatchStatus>($"/api/watches/{preflightSaved.Definition.Id}")).State == "stopped",
                "Saved watch preflight includes the exact definition revision and leaves polling stopped");
        }
        finally { await client.PostAsync<JsonElement>($"/api/watches/{preflightSaved.Definition.Id}/delete", new { }); }
        var templateSource = await client.PostAsync<WatchStatus>("/api/watches", preflightDraft with { Name = "Portable integration", Backend = "Sdk", Mode = "Offline", ControllerTargetId = null, IntervalMs = 375, HistoryLimit = 37 });
        WatchStatus? templateImported = null;
        try
        {
            var exportCall = new ExtensionCall(watches.Id, "studio_watch_template_export", watches.Generation,
                JsonSerializer.SerializeToElement(new { watchId = templateSource.Definition.Id, request = new WatchTemplateExportRequest(0) }, ProjectService.Json));
            var templateArtifact = await Tool<Artifact>("studio_extensions_apply", new() { ["call"] = exportCall });
            var templateBytes = await client.DownloadAsync(templateArtifact.Id); var portable = JsonSerializer.Deserialize<JsonElement>(templateBytes);
            Check(templateArtifact.Format == "json" && ProjectService.Hash(templateBytes) == templateArtifact.Sha256 &&
                portable.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "schemaVersion", "name", "tags", "intervalMs", "historyLimit", "backend" }.Order()),
                "MCP exports a verified portable JSON artifact containing only template fields");
            var templateRequest = new WatchTemplatePreviewRequest(portable, resolutionPlc.Id, resolutionPlc.CurrentSha256, Name: "Imported integration");
            var templateCall = new ExtensionCall(watches.Id, "studio_watch_template_preview", watches.Generation,
                JsonSerializer.SerializeToElement(new { request = templateRequest }, ProjectService.Json));
            var templatePreview = await Tool<WatchTemplatePreviewReport>("studio_extensions_read", new() { ["call"] = templateCall });
            var restTemplatePreview = await client.PostAsync<WatchTemplatePreviewReport>("/api/watch-templates/preview", templateRequest);
            Check(templatePreview.CanImport && JsonSerializer.Serialize(restTemplatePreview) == JsonSerializer.Serialize(templatePreview),
                "REST and MCP preview the same stopped-list binding independently of reader availability");
            var wrongReview = new ExtensionCall(watches.Id, "studio_watch_template_import", watches.Generation,
                JsonSerializer.SerializeToElement(new { request = new WatchTemplateImportRequest(templateRequest with { Name = "Changed after review" }, templatePreview.ReviewSha256) }, ProjectService.Json));
            Check((await mcp.CallToolAsync("studio_extensions_apply", new Dictionary<string, object?> { ["call"] = wrongReview })).IsError == true,
                "MCP template import rejects a changed binding under the old review hash");
            var importCall = wrongReview with { Arguments = JsonSerializer.SerializeToElement(new { request = new WatchTemplateImportRequest(templateRequest, templatePreview.ReviewSha256) }, ProjectService.Json) };
            templateImported = await Tool<WatchStatus>("studio_extensions_apply", new() { ["call"] = importCall });
            Check(templateImported.Definition.Id != templateSource.Definition.Id && templateImported.State == "stopped" && templateImported.Latest is null &&
                templateImported.Definition.Settings is { IntervalMs: 375, HistoryLimit: 37, Mode: "Offline", ControllerTargetId: null, SourcePath: null, SourceArtifactId: not null },
                "Reviewed MCP template import creates an independent stopped list with preserved polling settings and a new source artifact");
            var savedTemplate = await client.PostAsync<SavedArtifact>($"/api/artifacts/{templateArtifact.Id}/save", new SaveArtifactRequest(Path.Combine(root, "artifacts", "exports", "process.watch.json")));
            Check(savedTemplate.Sha256 == templateArtifact.Sha256 && ProjectService.Hash(await File.ReadAllBytesAsync(savedTemplate.Path)) == templateArtifact.Sha256 &&
                (await client.GetAsync<WatchStatus>($"/api/watches/{templateSource.Definition.Id}")).Definition.Revision == 0,
                "Portable JSON saves through the shared output-root guard and preserves the source list");
            var rebindDocument = XmlProjects.Parse(await File.ReadAllBytesAsync(preflightSource));
            rebindDocument.Descendants("Program").Single().SetAttributeValue("Name", "ReboundProgram");
            var rebindSource = Path.Combine(root, "examples", "RebindDestination.L5X");
            rebindDocument.Save(rebindSource);
            var rebindProject = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(rebindSource));
            var rebindRequest = new WatchRebindPreviewRequest(0, rebindProject.Id, rebindProject.CurrentSha256, [new("WatchProgram", "ReboundProgram")]);
            var rebindCall = new ExtensionCall(watches.Id, "studio_watch_rebind_preview", watches.Generation,
                JsonSerializer.SerializeToElement(new { watchId = templateImported.Definition.Id, request = rebindRequest }, ProjectService.Json));
            var reboundPreview = await Tool<WatchRebindPreviewReport>("studio_extensions_read", new() { ["call"] = rebindCall });
            var restReboundPreview = await client.PostAsync<WatchRebindPreviewReport>($"/api/watches/{templateImported.Definition.Id}/rebind-preview", rebindRequest);
            Check(reboundPreview.CanApply && reboundPreview.BaselineStatus == "matching_l5x" && reboundPreview.Changes.All(c => c.Changes.Contains("scope_changed")) &&
                JsonSerializer.Serialize(reboundPreview) == JsonSerializer.Serialize(restReboundPreview),
                "REST and MCP rebind preview compare the same approved source and exact mapped destination without opening a reader");
            var wrongRebind = new ExtensionCall(watches.Id, "studio_watch_rebind_apply", watches.Generation,
                JsonSerializer.SerializeToElement(new { watchId = templateImported.Definition.Id, request = new WatchRebindApplyRequest(rebindRequest, new string('0', 64)) }, ProjectService.Json));
            Check((await mcp.CallToolAsync("studio_extensions_apply", new Dictionary<string, object?> { ["call"] = wrongRebind })).IsError == true &&
                (await client.GetAsync<WatchStatus>($"/api/watches/{templateImported.Definition.Id}")).Definition.Revision == 0,
                "A rejected rebind review hash preserves the existing definition revision");
            var applyRebind = wrongRebind with { Arguments = JsonSerializer.SerializeToElement(new { watchId = templateImported.Definition.Id,
                request = new WatchRebindApplyRequest(rebindRequest, reboundPreview.ReviewSha256) }, ProjectService.Json) };
            var rebound = await Tool<WatchStatus>("studio_extensions_apply", new() { ["call"] = applyRebind });
            var oldRecording = await client.PostAsync<WatchRecordingPage>($"/api/watches/{rebound.Definition.Id}/recording/query", new WatchRecordingQuery(WatchRevision: 0));
            var newRecording = await client.PostAsync<WatchRecordingPage>($"/api/watches/{rebound.Definition.Id}/recording/query", new WatchRecordingQuery());
            Check(rebound.Definition.Id == templateImported.Definition.Id && rebound.Definition.Revision == 1 && rebound.State == "stopped" && rebound.Latest is null &&
                rebound.Definition.Settings is { IntervalMs: 375, HistoryLimit: 37 } && rebound.Definition.Settings.Tags.All(t => t.ProgramName == "ReboundProgram") &&
                oldRecording.Definition!.Settings.Tags.All(t => t.ProgramName == "WatchProgram") && newRecording.Records.Any(r => r.Kind == "source_rebound"),
                "Reviewed MCP rebind advances the same stopped list and preserves its settings, old recording definition and cutover event");
            Check((await mcp.CallToolAsync("studio_extensions_apply", new Dictionary<string, object?> { ["call"] = applyRebind })).IsError == true,
                "Reusing an applied rebind request is rejected by the expected watch revision");
        }
        finally
        {
            if (templateImported is not null) await client.PostAsync<JsonElement>($"/api/watches/{templateImported.Definition.Id}/delete", new { });
            await client.PostAsync<JsonElement>($"/api/watches/{templateSource.Definition.Id}/delete", new { });
        }
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
