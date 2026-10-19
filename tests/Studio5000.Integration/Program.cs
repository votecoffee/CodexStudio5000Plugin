using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.IO.Compression;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;

var root = ServiceClient.FindRoot();
if (args.Length == 3 && args[0] == "--hmi-language-offline") { await HmiLanguageIntegration.ValidateFileAsync(root, Path.GetFullPath(args[1]), args[2]); return; }
if (args.Length == 3 && args[0] == "--sdk-version-smoke") { await LogixVersionSmoke.RunAsync(root, int.Parse(args[1]), Path.GetFullPath(args[2])); return; }
if (args.Length == 2 && args[0] == "--sdk-offline-path") { await SdkOfflinePath.RunAsync(root, Path.GetFullPath(args[1])); return; }
if (args.Length == 2 && args[0] == "--value-merge-native-smoke") { await ValueMergeNativeSmoke.RunAsync(root, Path.GetFullPath(args[1])); return; }
if (args.Length == 1 && args[0] == "--online-plan-only") { await OnlineEditPlanIntegration.RunAsync(root); return; }
using var client = new ServiceClient(root);
var checks = new List<string>();
void Check(bool valid, string name) { if (!valid) throw new Exception("FAIL: " + name); checks.Add(name); Console.WriteLine("PASS: " + name); }
using var connection = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".local", "connection.json")));
using var raw = new HttpClient { BaseAddress = new Uri(connection.RootElement.GetProperty("url").GetString()!) };
Check((await raw.GetAsync("/api/projects")).StatusCode == HttpStatusCode.Unauthorized, "API rejects unauthenticated clients");
raw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(connection.RootElement.GetProperty("tokenFile").GetString()!).Trim());
raw.DefaultRequestHeaders.Add("Origin", "https://example.org");
Check((await raw.GetAsync("/api/projects")).StatusCode == HttpStatusCode.Forbidden, "API rejects browser origins");
raw.DefaultRequestHeaders.Remove("Origin");
var source = Path.Combine(root, "examples", "Demo.L5X");
var sourceHash = ProjectService.Hash(File.ReadAllBytes(source));
var p = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(source));
var draft = await client.PostAsync<ChangePreview>($"/api/projects/{p.Id}/previews", new PreviewRequest(p.CurrentSha256,
    [new Edit("set_text", "//Tag[@Name='CycleCount']/Description", "Shared REST edit — verified 零")]));
Check((await client.GetAsync<ProjectInfo>($"/api/projects/{p.Id}")).Revision == 0, "REST preview leaves working revision unchanged");
p = await client.PostAsync<ProjectInfo>($"/api/projects/{p.Id}/previews/{draft.Id}/apply", new ApplyRequest(draft.BaseSha256));
Check(p.Revision == 1, "REST apply creates a revision");
var artifact = await client.PostAsync<Artifact>($"/api/projects/{p.Id}/exports", new ExportRequest(p.CurrentSha256));
Check(ProjectService.Hash(await client.DownloadAsync(artifact.Id)) == p.CurrentSha256, "REST download matches revision SHA-256");
var destination = Path.Combine(root, "artifacts", "exports", Guid.NewGuid().ToString("N") + ".l5x");
var saved = await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(destination));
Check(ProjectService.Hash(File.ReadAllBytes(saved.Path)) == artifact.Sha256, "Service saves a new artifact with matching hash");
try { await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(destination)); throw new Exception("Existing output was overwritten."); }
catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.BadRequest) { checks.Add("Existing outputs are preserved"); }

await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "powershell.exe",
    Arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "Start-Mcp.ps1")],
    EnvironmentVariables = new Dictionary<string, string?> { ["STUDIO5000_PLUGIN_ROOT"] = root },
    Name = "Studio 5000 integration"
}));
var tools = await mcp.ListToolsAsync();
Console.WriteLine($"MCP server: {mcp.ServerInfo.Name} {mcp.ServerInfo.Version}");
Check(tools.Count == 56, "Official MCP client discovers 46 compatible tools plus 10 stable extension tools");
if (args.Length == 1 && args[0] == "--browser-recording-only")
{
    checks.AddRange(await BrowserRecordingIntegration.RunAsync(root, client, mcp));
    var report = Path.Combine(root, "artifacts", "test-results", "browser-recording-smoke.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed = checks.Count, checks, serverInfo = mcp.ServerInfo, controllerConnectionAttempted = false, nativeBridgeExecuted = false }, ProjectService.Json));
    Console.WriteLine($"{checks.Count} browser/recording smoke checks passed; no native bridge or controller operation."); return;
}
var sdkCatalog = await mcp.CallToolAsync("studio_sdk_catalog", new Dictionary<string, object?>());
Check(sdkCatalog.IsError != true && sdkCatalog.Content.OfType<TextContentBlock>().Any(b => b.Text.Contains("PartialImportRungsFromXmlFileAsync")), "MCP exposes the complete SDK catalog through the shared service");
var editCapabilities = (await client.GetAsync<JsonElement>("/api/sdk/capabilities")).GetProperty("onlineEdits");
Check(!editCapabilities.GetProperty("standaloneTestEdits").GetBoolean() && !editCapabilities.GetProperty("controllerTestModeIsTestEdits").GetBoolean(), "Catalog explicitly distinguishes native Test Edits from controller Test mode");
if ((await client.GetAsync<JsonElement>("/api/sdk/capabilities")).GetProperty("nativeBridgeConfigured").GetBoolean())
{
    var sdkStart = await mcp.CallToolAsync("studio_sdk_start_job", new Dictionary<string, object?>
    { ["calls"] = new[] { new { method = "GetProcessorTypesAsync", arguments = new { majorRev = 35 } } } });
    Check(sdkStart.IsError != true, "MCP submits a native SDK query job");
    var sdkJob = JsonSerializer.Deserialize<SdkJob>(sdkStart.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
    for (var attempt = 0; attempt < 480 && sdkJob.Status is "queued" or "running"; attempt++)
    { await Task.Delay(500); sdkJob = await client.GetAsync<SdkJob>("/api/sdk/jobs/" + sdkJob.Id); }
    Check(sdkJob.Status == "succeeded" && sdkJob.Steps[0].Value!.Value.GetArrayLength() > 0, "REST observes the native SDK result submitted through MCP");
}
var inspected = await mcp.CallToolAsync("studio_inspect_project", new Dictionary<string, object?> { ["projectId"] = p.Id, ["kind"] = "Tag" });
Check(inspected.IsError != true && inspected.Content.OfType<TextContentBlock>().Any(b => b.Text.Contains("CycleCount")), "MCP reads the project edited through REST");
var previewResult = await mcp.CallToolAsync("studio_preview_changes", new Dictionary<string, object?>
{
    ["projectId"] = p.Id, ["expectedSha256"] = p.CurrentSha256,
    ["edits"] = new[] { new Edit("set_text", "//Tag[@Name='CycleCount']/Description", "Shared MCP edit") }
});
Check(previewResult.IsError != true, "MCP creates an edit preview");
var previewText = previewResult.Content.OfType<TextContentBlock>().First().Text;
var mcpDraft = JsonSerializer.Deserialize<ChangePreview>(previewText, ProjectService.Json)!;
var applied = await mcp.CallToolAsync("studio_apply_changes", new Dictionary<string, object?>
{
    ["projectId"] = p.Id, ["previewId"] = mcpDraft.Id, ["expectedSha256"] = p.CurrentSha256
});
Check(applied.IsError != true, "MCP applies a reviewed preview");
var latest = await client.GetAsync<ProjectInfo>($"/api/projects/{p.Id}");
Check(latest.Revision == 2 && latest.CurrentSha256 == mcpDraft.ResultSha256, "REST observes the MCP revision in the same service");
Check(ProjectService.Hash(File.ReadAllBytes(source)) == sourceHash, "REST and MCP edits preserve original source bytes");
try
{
    await client.PostAsync<ChangePreview>($"/api/projects/{p.Id}/previews", new PreviewRequest(p.CurrentSha256, [new("set_text", "//Description", "stale")]));
    throw new Exception("Stale write accepted.");
}
catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { checks.Add("REST rejects stale writes after MCP edits"); }
var history = await client.GetAsync<AuditEvent[]>("/api/audit?projectId=" + p.Id);
Check(history.Count(e => e.Action == "apply") == 2, "Both clients produce shared audit evidence");
var ftStatus = await client.GetAsync<JsonElement>("/api/factorytalk/status");
var schemaDirectory = ftStatus.GetProperty("schemaDirectory").GetString()!;
var graphicsChecks = new List<object>();
if (Directory.Exists(schemaDirectory))
{
    foreach (var edition in new[] { "ME", "SE" })
    {
        var file = Path.Combine(root, "examples", "Demo" + edition + ".xml");
        var before = ProjectService.Hash(File.ReadAllBytes(file));
        var display = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(file));
        Check(display.Kind == "ftview-" + edition.ToLowerInvariant(), edition + " display is detected through REST");
        var validation = await client.GetAsync<GraphicsValidation>($"/api/projects/{display.Id}/graphics-validation");
        Check(validation.SchemaValidated, edition + " fixture validates against the installed Rockwell XSD");
        var caption = edition + " shared-service verification 零";
        var graphicsPreview = await mcp.CallToolAsync("ftview_preview_changes", new Dictionary<string, object?>
        {
            ["projectId"] = display.Id, ["expectedSha256"] = display.CurrentSha256,
            ["edits"] = new[] { new Edit("set_attribute", "/gfx/text[@name='Title']", caption, "caption"),
                new Edit("set_attribute", "//connection[@name='Value']", "{[PLC]Demo.VerifiedCount}", "expression") }
        });
        Check(graphicsPreview.IsError != true, edition + " MCP previews caption and tag-connection edits");
        var gd = JsonSerializer.Deserialize<ChangePreview>(graphicsPreview.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        var graphicsApply = await mcp.CallToolAsync("ftview_apply_changes", new Dictionary<string, object?>
        { ["projectId"] = display.Id, ["previewId"] = gd.Id, ["expectedSha256"] = display.CurrentSha256 });
        Check(graphicsApply.IsError != true, edition + " MCP applies to the shared working revision");
        display = await client.GetAsync<ProjectInfo>($"/api/projects/{display.Id}");
        var plan = await client.PostAsync<GraphicsImportPlan>($"/api/projects/{display.Id}/graphics-imports",
            new PrepareGraphicsImportRequest(display.CurrentSha256, "CodexPlugin_" + edition + "_Fixture", "Main", "en-US"));
        Check(plan.Changes.Length == 2 && plan.NativeImportStatus == "pending", edition + " import plan binds two edits and leaves native import pending");
        File.Copy(plan.XmlPath, plan.ReexportPath);
        var graphicsVerify = await mcp.CallToolAsync("ftview_verify_import", new Dictionary<string, object?> { ["planId"] = plan.Id });
        Check(graphicsVerify.IsError != true, edition + " MCP checks a supplied export file");
        var receipt = JsonSerializer.Deserialize<GraphicsVerification>(graphicsVerify.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        Check(receipt.Status == "property_values_match" && receipt.NativeImportStatus == "not_observed_by_service", edition + " matching XML does not claim native acceptance");
        Check(ProjectService.Hash(File.ReadAllBytes(file)) == before, edition + " original display XML bytes are preserved");
        graphicsChecks.Add(new { edition, validation.SchemaFile, validation.SchemaSha256, sourceSha256 = before, planId = plan.Id,
            simulatedExport = true, nativeImport = "not tested; Computer Use stopped by user" });
    }
}
else Console.WriteLine("Installed FactoryTalk schema checks skipped: schema directory unavailable.");
checks.AddRange(await OnlineEditPlanIntegration.RunAsync(root));
checks.AddRange(await WatchIntegration.RunAsync(root, client, mcp));
checks.AddRange(await ViewDesignerIntegration.RunAsync(root, client, mcp));
checks.AddRange(await ExtensionIntegration.RunAsync(root, client, mcp));
checks.AddRange(await EngineeringUtilitiesIntegration.RunAsync(root, client, mcp));
checks.AddRange(await SavedReviewIntegration.RunAsync(root, client, mcp));
checks.AddRange(await ComparisonReviewIntegration.RunAsync(root, client, mcp));
checks.AddRange(await DiagnosticWorkflowsIntegration.RunAsync(root, client, mcp));
checks.AddRange(await DocumentationIntegration.RunAsync(root, client, mcp));
checks.AddRange(await HmiLanguageIntegration.RunAsync(root, client, mcp));
checks.AddRange(await TransactionIntegration.RunAsync(root, client, mcp));
checks.AddRange(await BrowserRecordingIntegration.RunAsync(root, client, mcp));
Directory.CreateDirectory(Path.Combine(root, "artifacts", "test-results"));
await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "test-results", "integration.json"),
    JsonSerializer.Serialize(new { passed = checks.Count, checks, graphicsChecks, projectId = p.Id, latest.CurrentSha256, sourceHash }, ProjectService.Json));
Console.WriteLine($"{checks.Count} integration checks passed.");

if (args.Length == 2 && args[0] == "--factorytalk-corpus")
{
    using var archive = ZipFile.OpenRead(args[1]);
    var corpus = new List<object>(); var failed = 0; var passed = 0; var skipped = 0;
    foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
    {
        using var stream = entry.Open(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        try
        {
            var doc = FactoryTalkGraphics.Parse(bytes);
            var validation = FactoryTalkGraphics.Validate(doc, schemaDirectory, requireSchema: true);
            corpus.Add(new { file = entry.FullName, sha256 = ProjectService.Hash(bytes), validation, status = "passed" }); passed++;
        }
        catch (OperationException ex) when (ex.Code == "invalid_graphics")
        { corpus.Add(new { file = entry.FullName, status = "non-display XML", detail = ex.Message }); skipped++; }
        catch (Exception ex) { corpus.Add(new { file = entry.FullName, status = "failed", detail = ex.Message }); failed++; }
    }
    await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "test-results", "factorytalk-corpus.json"),
        JsonSerializer.Serialize(new { archiveSha256 = ProjectService.Hash(await File.ReadAllBytesAsync(args[1])), passed, failed, skipped, files = corpus }, ProjectService.Json));
    Console.WriteLine($"FactoryTalk corpus: {passed} passed, {failed} failed, {skipped} non-display files.");
    if (failed > 0) throw new Exception("FactoryTalk corpus validation failed; inspect factorytalk-corpus.json.");
}
