using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class DocumentationIntegration
{
    internal static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool valid, string message) { if (!valid) throw new Exception("FAIL: " + message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        var module = (await client.GetAsync<ExtensionCatalog>("/api/extensions")).Extensions.Single(x => x.Id == "logix");
        var p = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(Path.Combine(root, "examples", "MultilingualDemo.L5X")));
        async Task<CallToolResult> Call(string operation, object request, bool write = false) => await mcp.CallToolAsync(write ? "studio_extensions_apply" : "studio_extensions_read", new Dictionary<string, object?>
        { ["call"] = new ExtensionCall("logix", operation, module.Generation, JsonSerializer.SerializeToElement(new { projectId = p.Id, request }, ProjectService.Json)) });
        async Task<T> Tool<T>(string operation, object request, bool write = false)
        { var result = await Call(operation, request, write); if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text); return JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!; }
        var before = XDocument.Load(p.SourcePath); var all = await Tool<DocumentationPage>("studio_documentation_browse", new DocumentationQuery(p.CurrentSha256));
        var rest = await client.PostAsync<DocumentationPage>($"/api/projects/{p.Id}/documentation/browse", new DocumentationQuery(p.CurrentSha256));
        Check(JsonSerializer.Serialize(rest) == JsonSerializer.Serialize(all) && all.Fields.Any(f => f.OwnerKind == "Member") && all.Fields.Any(f => f.OwnerKind == "Parameter"), "REST and MCP extract the same multilingual tag/rung/UDT/AOI documentation");
        var legacy = all.Fields.Single(f => f.Name == "Enabled"); Check(legacy.Texts.Single().Language is null && all.DefaultLanguage == "en-US", "Plain descriptions retain an explicit unlocalized identity despite a configured default language");
        var record = all.Fields.Single(f => f.Name == "Record" && f.Kind == "description"); var rung = all.Fields.Single(f => f.Kind == "rung-comment");
        var request = new DocumentationPreviewRequest(p.CurrentSha256, [new(record.OwnerXPath, "description", "es-ES", "Producción actualizada 零\n<text> & ]]>"), new(rung.OwnerXPath, "rung-comment", "ja-JP", "出力許可")]);
        var preview = await Tool<DocumentationPreview>("studio_documentation_preview", request);
        var restPreview = await client.PostAsync<DocumentationPreview>($"/api/projects/{p.Id}/documentation/preview", request);
        Check(preview.ReviewSha256 == restPreview.ReviewSha256 && preview.Changes.Length == 2 && (await client.GetAsync<ProjectInfo>("/api/projects/" + p.Id)).Revision == 0, "Documentation previews agree across transports and leave the managed revision unchanged");
        Check((await Call("studio_documentation_apply", new DocumentationApplyRequest(request, preview.ReviewSha256))).IsError == true && (await Call("studio_documentation_apply", new DocumentationApplyRequest(request, "wrong"), true)).IsError == true,
            "Documentation apply rejects read-only dispatch and an unrelated review hash");
        var updated = await Tool<ProjectInfo>("studio_documentation_apply", new DocumentationApplyRequest(request, preview.ReviewSha256), true);
        var after = await client.PostAsync<DocumentationPage>($"/api/projects/{p.Id}/documentation/browse", new DocumentationQuery(updated.CurrentSha256));
        Check(updated.Revision == 1 && after.Fields.Single(f => f.Kind == "rung-comment").Texts.Any(t => t.Language == "es-ES") && after.Fields.Single(f => f.Kind == "rung-comment").Texts.Any(t => t.Language == "ja-JP"),
            "MCP applies a separate managed revision while preserving existing rung translations");
        Check((await Call("studio_documentation_apply", new DocumentationApplyRequest(request, preview.ReviewSha256), true)).IsError == true, "Replaying documentation edits against an old project hash is rejected");
        var promoting = new DocumentationPreviewRequest(updated.CurrentSha256, [new(legacy.OwnerXPath, "description", "fr-FR", "Autorisation")]);
        Check((await Call("studio_documentation_preview", promoting)).IsError == true, "Adding a translation cannot silently relabel existing plain text");
        promoting = promoting with { Edits = [promoting.Edits[0] with { LegacyLanguage = "en-US" }, new(legacy.OwnerXPath, "description", "zh-CN", "运行许可")] };
        var promotion = await Tool<DocumentationPreview>("studio_documentation_preview", promoting); p = await Tool<ProjectInfo>("studio_documentation_apply", new DocumentationApplyRequest(promoting, promotion.ReviewSha256), true);
        var promoted = await Tool<DocumentationPage>("studio_documentation_browse", new DocumentationQuery(p.CurrentSha256, Query: "Enabled"));
        Check(promoted.Fields.Single().Texts.Length == 3 && promoted.Fields.Single().Texts.Any(t => t.Language == "en-US" && t.Text.Contains("Legacy description")), "Explicit legacy-language promotion retains the original text while adding multiple translations atomically");
        var exportRequest = new DocumentationExportRequest(new(p.CurrentSha256, Offset: 1, Limit: 1));
        Check((await Call("studio_documentation_export", exportRequest)).IsError == true, "Read-only MCP dispatch rejects documentation artifact creation");
        var artifact = await Tool<Artifact>("studio_documentation_export", exportRequest, true); var bytes = await client.DownloadAsync(artifact.Id);
        var document = JsonSerializer.Deserialize<DocumentationExportDocument>(bytes, ProjectService.Json)!;
        Check(document.Fields.Length > 1 && document.Project.Sha256 == p.CurrentSha256 && ProjectService.Hash(bytes) == artifact.Sha256, "Full multilingual JSON export ignores browser pagination and retains the exact source revision/hash");
        var exported = await client.PostAsync<Artifact>($"/api/projects/{p.Id}/exports", new ExportRequest(p.CurrentSha256)); var final = XDocument.Parse(System.Text.Encoding.UTF8.GetString(await client.DownloadAsync(exported.Id)));
        Check(before.Descendants("Text").Select(e => e.Value).SequenceEqual(final.Descendants("Text").Select(e => e.Value)) && before.Descendants("Data").Select(e => e.ToString()).SequenceEqual(final.Descendants("Data").Select(e => e.ToString())) &&
            before.Root!.Element("Controller")!.Attributes().Select(a => a.ToString()).SequenceEqual(final.Root!.Element("Controller")!.Attributes().Select(a => a.ToString())), "Documentation updates preserve logic, saved values and project language configuration");
        Check(ProjectService.Hash(File.ReadAllBytes(p.SourcePath)) == p.SourceSha256 && (await client.GetAsync<SdkJob[]>("/api/sdk/jobs")).All(j => j.ControllerEffectsPossible == false), "Documentation workflows preserve original source bytes and perform no controller operation");
        return checks;
    }
}
