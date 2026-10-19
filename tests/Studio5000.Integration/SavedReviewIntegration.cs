using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class SavedReviewIntegration
{
    public static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        var catalog = await client.GetAsync<ExtensionCatalog>("/api/extensions"); var logix = catalog.Extensions.Single(e => e.Id == "logix");
        async Task<CallToolResult> Call(string name, object? request = null, bool write = false, string? key = null, string? id = null)
        {
            var inner = new Dictionary<string, object?>(); if (request is not null) inner["request"] = request; if (key is not null) inner[key] = id;
            return await mcp.CallToolAsync(write ? "studio_extensions_apply" : "studio_extensions_read", new Dictionary<string, object?>
            { ["call"] = new ExtensionCall("logix", name, logix.Generation, JsonSerializer.SerializeToElement(inner, ProjectService.Json)) });
        }
        async Task<T> Tool<T>(string name, object? request = null, bool write = false, string? key = null, string? id = null)
        {
            var result = await Call(name, request, write, key, id);
            if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text);
            return JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        }
        var doc = XDocument.Load(Path.Combine(root, "examples", "WatchDemo.L5X"));
        var program = doc.Descendants("Program").Single(); program.Element("Routines")?.Remove();
        program.Add(new XElement("Routines", new XElement("Routine", new XAttribute("Name", "main"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "NOP();"))))));
        var leftPath = Path.Combine(root, "examples", "SavedReviewLeft.l5x"); var rightPath = Path.Combine(root, "examples", "SavedReviewRight.l5x"); doc.Save(leftPath);
        LogixStructures.Tag(doc, "Enabled", "WatchProgram").Descendants("DataValue").Single().SetAttributeValue("Value", "0"); doc.Save(rightPath);
        var a = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(leftPath)); var b = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(rightPath));
        var comparison = new ProjectCompareRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, "Tag");
        var routine = (await client.PostAsync<ComparedRoutinesReport>("/api/projects/compare/routines", comparison with { Query = null })).Routines.Single();
        var save = new ComparisonSessionSaveRequest("Generated session", comparison, new(routine.LeftXPath, routine.RightXPath));
        Check((await Call("studio_comparison_session_save", save)).IsError == true, "Read-only MCP dispatch rejects saved-session mutation before creating a definition");
        var saved = await Tool<ComparisonSession>("studio_comparison_session_save", save, true);
        var view = await client.GetAsync<ComparisonSessionView>("/api/comparison-sessions/" + saved.Id);
        Check(view.CanResume && view.Session == saved && (await Tool<ReviewSummary[]>("studio_comparison_sessions")).Any(s => s.Id == saved.Id),
            "MCP saves pinned comparison filters/bookmarks and REST reads the same persisted definition");
        Check((await Call("studio_comparison_session_save", save with { Id = saved.Id, ExpectedRevision = 9 }, true)).IsError == true,
            "Stale session updates are rejected across MCP without overwriting the current definition");
        var template = JsonSerializer.SerializeToElement(new MergeMappingTemplate(1, "Reusable generated mapping", [new("Enabled", "Enabled")]), ProjectService.Json);
        var mapping = await Tool<SavedMergeMapping>("studio_merge_mapping_save", new MergeMappingSaveRequest(template), true);
        var read = await Tool<SavedMergeMapping>("studio_merge_mapping_get", key: "mappingId", id: mapping.Id);
        Check(read.Revision == 0 && read.Template.Tags.SequenceEqual(mapping.Template.Tags) && (await client.GetAsync<ReviewSummary[]>("/api/merge-mappings")).Any(m => m.Id == mapping.Id),
            "REST and MCP share reusable name-pair mappings without project bindings or copied values");
        var bind = new MergeMappingBindRequest(mapping.Id, 0, a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, "WatchProgram", "WatchProgram");
        var binding = await Tool<MergeMappingBinding>("studio_merge_mapping_bind", bind);
        var rest = await client.PostAsync<MergeMappingBinding>("/api/merge-mappings/bind", bind);
        Check(binding.Preview.CanApply && binding.Preview.ReviewSha256 == rest.Preview.ReviewSha256 && (await client.GetAsync<ProjectInfo>("/api/projects/" + b.Id)).Revision == 0,
            "Binding a mapping through REST or MCP returns the same fresh review and leaves the destination unchanged");
        Check((await Call("studio_merge_mapping_bind", bind with { ExpectedRevision = 1 })).IsError == true,
            "Mapping binding rejects an outdated definition revision before preparing a value request");
        Check((await Call("studio_value_merge_apply", new TagValueMergeApplyRequest(binding.Proposed, "wrong"), true)).IsError == true,
            "Using a saved mapping still requires the exact fresh value review hash to apply");
        var merged = await Tool<ProjectInfo>("studio_value_merge_apply", new TagValueMergeApplyRequest(binding.Proposed, binding.Preview.ReviewSha256), true);
        var stale = await Tool<ComparisonSessionView>("studio_comparison_session_get", key: "sessionId", id: saved.Id);
        Check(merged.Revision == 1 && !stale.CanResume && stale.Session.Comparison.RightSha256 == b.CurrentSha256,
            "An offline merge makes the pinned session stale without silently rebinding its saved snapshot hash");
        Check((await Call("studio_merge_mapping_bind", bind)).IsError == true, "An old mapping binding rejects changed destination hashes after the merge");
        await Tool<JsonElement>("studio_merge_mapping_delete", new ReviewDeleteRequest(0), true, "mappingId", mapping.Id);
        await Tool<JsonElement>("studio_comparison_session_delete", new ReviewDeleteRequest(0), true, "sessionId", saved.Id);
        Check((await Call("studio_merge_mapping_get", key: "mappingId", id: mapping.Id)).IsError == true &&
            (await client.GetAsync<ProjectInfo>("/api/projects/" + b.Id)).Revision == 1, "Deleting saved definitions preserves the merged project and its revision");
        Check(ProjectService.Hash(await File.ReadAllBytesAsync(leftPath)) == a.SourceSha256 && ProjectService.Hash(await File.ReadAllBytesAsync(rightPath)) == b.SourceSha256,
            "Saved-review and mapping operations preserve both original input files");
        return checks;
    }
}
