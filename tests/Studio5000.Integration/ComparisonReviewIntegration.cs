using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class ComparisonReviewIntegration
{
    public static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>(); void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        var logix = (await client.GetAsync<ExtensionCatalog>("/api/extensions")).Extensions.Single(e => e.Id == "logix");
        async Task<CallToolResult> Call(string name, object arguments, bool apply = false) => await mcp.CallToolAsync(apply ? "studio_extensions_apply" : "studio_extensions_read",
            new Dictionary<string, object?> { ["call"] = new ExtensionCall("logix", name, logix.Generation, JsonSerializer.SerializeToElement(arguments, ProjectService.Json)) });
        async Task<T> Tool<T>(string name, object arguments, bool apply = false)
        {
            var result = await Call(name, arguments, apply); var text = result.Content.OfType<TextContentBlock>().First().Text;
            if (result.IsError == true) throw new Exception(text); return JsonSerializer.Deserialize<T>(text, ProjectService.Json)!;
        }
        var controller = new XElement("Controller", new XAttribute("Name", "Generated"), new XElement("Tags", Enumerable.Range(0, 210).Select(i =>
            new XElement("Tag", new XAttribute("Name", "Value" + i.ToString("D3")), new XAttribute("DataType", "DINT"), new XElement("DataValue", new XAttribute("Value", "1"))))));
        var rung = new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "XIC(Start)OTE(Output);"));
        controller.Add(new XElement("Programs", new XElement("Program", new XAttribute("Name", "Example"), new XElement("Routines", new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"), new XElement("RLLContent", rung))))));
        var doc = new XDocument(new XElement("RSLogix5000Content", controller));
        var aPath = Path.Combine(root, "examples", "ReviewExportLeft.l5x"); var bPath = Path.Combine(root, "examples", "ReviewExportRight.l5x"); doc.Save(aPath);
        foreach (var value in doc.Descendants("DataValue")) value.SetAttributeValue("Value", "2"); rung.Element("Text")!.Value = "XIO(Start)OTE(Output);"; doc.Save(bPath);
        var a = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(aPath)); var b = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(bPath));
        var comparison = new ProjectCompareRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, Offset: 200, Limit: 1);
        var request = new ComparisonReviewExportRequest(comparison, "json");
        Check((await Call("studio_comparison_review_export", new { request })).IsError == true, "Read-only MCP dispatch rejects report artifact/history creation");
        var exported = await Tool<ComparisonReviewExport>("studio_comparison_review_export", new { request }, true);
        var bytes = await client.DownloadAsync(exported.Artifact.Id); var report = JsonSerializer.Deserialize<ComparisonReviewDocument>(bytes, ProjectService.Json)!;
        Check(report.Changes.Length == 211 && report.Changes.All(c => c.Difference.LeftXml is null && c.Difference.RightXml is null) && !System.Text.Encoding.UTF8.GetString(bytes).Contains("sourcePath", StringComparison.OrdinalIgnoreCase),
            "MCP report export includes all matches beyond the visible page and omits XML/source paths by default");
        var history = await client.GetAsync<ComparisonReviewPage>("/api/comparison-reviews?limit=1&query=" + exported.Review.Id);
        var entry = await Tool<ComparisonReviewEntry>("studio_comparison_review_get", new { reviewId = exported.Review.Id });
        Check(history.Total == 1 && history.Reviews.Single().Id == entry.Id && entry.Sha256 == ProjectService.Hash(bytes),
            "REST history and MCP metadata share the immutable report and its independently downloaded artifact hash");
        var html = await client.PostAsync<ComparisonReviewExport>("/api/comparison-reviews/export", request with { Format = "html", IncludeXml = true, IncludeLadder = true });
        var htmlBytes = await client.DownloadAsync(html.Artifact.Id); var content = System.Text.Encoding.UTF8.GetString(htmlBytes);
        var saved = await Tool<SavedArtifact>("studio_save_artifact", new { artifactId = html.Artifact.Id, request = new SaveArtifactRequest(Path.Combine(root, "artifacts", "exports", "comparison-review.html")) }, true);
        Check(content.Contains("<svg") && content.Contains("Content-Security-Policy") && saved.Sha256 == html.Artifact.Sha256 && ProjectService.Hash(await File.ReadAllBytesAsync(saved.Path)) == saved.Sha256,
            "REST exports optional passive ladder drawings/XML to standalone HTML and MCP saves its verified copy");
        var draft = await client.PostAsync<ChangePreview>($"/api/projects/{b.Id}/previews", new PreviewRequest(b.CurrentSha256,
            [new("set_attribute", "/RSLogix5000Content/Controller/Tags/Tag[@Name='Value000']/DataValue", "3", "Value")]));
        var changed = await client.PostAsync<ProjectInfo>($"/api/projects/{b.Id}/previews/{draft.Id}/apply", new ApplyRequest(b.CurrentSha256));
        var old = await client.GetAsync<ComparisonReviewEntry>("/api/comparison-reviews/" + entry.Id);
        Check(changed.Revision == 1 && old.Right.Sha256 == b.CurrentSha256 && (await Call("studio_comparison_review_export", new { request }, true)).IsError == true,
            "Retained reports remain readable after project changes while a new export rejects outdated snapshot hashes");
        var listed = await Tool<ComparisonReviewPage>("studio_comparison_reviews", new { query = exported.Review.Id, offset = 0, limit = 1 });
        Check(listed.Total == 1 && (await Call("studio_comparison_review_forget", new { reviewId = entry.Id, request = new ComparisonReviewDeleteRequest("wrong") }, true)).IsError == true,
            "MCP report history filters/paging agree with REST and forgetting requires the recorded artifact hash");
        await Tool<JsonElement>("studio_comparison_review_forget", new { reviewId = entry.Id, request = new ComparisonReviewDeleteRequest(entry.Sha256) }, true);
        Check((await client.GetAsync<ComparisonReviewPage>("/api/comparison-reviews?query=" + entry.Id)).Total == 0 && (await client.DownloadAsync(entry.ArtifactId)).SequenceEqual(bytes),
            "Forgetting a report history entry preserves the immutable report artifact");
        Check(ProjectService.Hash(await File.ReadAllBytesAsync(aPath)) == a.SourceSha256 && ProjectService.Hash(await File.ReadAllBytesAsync(bPath)) == b.SourceSha256,
            "Report export/history operations preserve the original source files");
        return checks;
    }
}
