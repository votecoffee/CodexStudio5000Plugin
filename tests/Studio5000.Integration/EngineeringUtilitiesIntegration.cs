using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class EngineeringUtilitiesIntegration
{
    public static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        var raw = await mcp.CallToolAsync("studio_extensions_catalog", new Dictionary<string, object?>());
        var extension = JsonSerializer.Deserialize<ExtensionCatalog>(raw.Content.OfType<TextContentBlock>().First().Text, ExtensionProtocol.Json)!.Extensions.Single(e => e.Id == "logix");
        async Task<CallToolResult> Call(string operation, object request, bool write = false) => await mcp.CallToolAsync(write ? "studio_extensions_apply" : "studio_extensions_read",
            new Dictionary<string, object?> { ["call"] = new ExtensionCall(extension.Id, operation, extension.Generation, JsonSerializer.SerializeToElement(new { request }, ProjectService.Json)) });
        async Task<T> Tool<T>(string operation, object request, bool write = false)
        {
            var result = await Call(operation, request, write); if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text);
            return JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        }
        var sourcePath = Path.Combine(root, "examples", "ValueMergeSource.L5X"); var targetPath = Path.Combine(root, "examples", "ValueMergeTarget.L5X");
        var doc = XmlProjects.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "examples", "WatchDemo.L5X")));
        doc.Descendants("Routine").Single().Add(new XElement("RLLContent",
            new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "XIC(Enabled)ADD(Record.Count,1,Record.Inner.Count);")),
            new XElement("Rung", new XAttribute("Number", "1"), new XElement("Text", "NOP();")))); doc.Save(sourcePath);
        var record = doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Record"); record.Descendants("DataValueMember").First().SetAttributeValue("Value", "999"); record.Add(new XElement("Description", "Destination metadata"));
        doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Enabled").Descendants("DataValue").Single().SetAttributeValue("Value", "0");
        foreach (var rung in doc.Descendants("Rung")) rung.SetAttributeValue("Number", int.Parse((string)rung.Attribute("Number")!) + 1);
        doc.Descendants("RLLContent").Single().AddFirst(new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "XIO(Enabled)NOP();"))); doc.Save(targetPath);
        var a = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(sourcePath)); var b = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(targetPath));
        var compare = new ProjectCompareRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256);
        var routines = await Tool<ComparedRoutinesReport>("studio_compare_routines", compare); var routine = routines.Routines.Single();
        Check(routine is { Owner: "Program:WatchProgram", Name: "main", Change: "modified", LeftRungs: 2, RightRungs: 3 }, "MCP routine browser reports independent saved-project ladder differences");
        Check(JsonSerializer.Serialize(routines) == JsonSerializer.Serialize(await client.PostAsync<ComparedRoutinesReport>("/api/projects/compare/routines", compare)), "REST and MCP share the hash-bound routine browser");
        var ladder = new LadderCompareRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, routine.LeftXPath, routine.RightXPath, Limit: 3);
        var aligned = await Tool<LadderComparisonReport>("studio_compare_ladder", ladder);
        Check(aligned.Total == 3 && aligned.Rungs[0] is { Change: "added", Left: null, Right.Diagram: not null } && aligned.Rungs[1] is { Change: "unchanged", Left.Index: 0, Right.Index: 1 }, "Ladder comparison exposes insertion gaps and aligns following saved rungs with both diagrams");
        var changed = await client.PostAsync<LadderComparisonReport>("/api/projects/compare/ladder", ladder with { ChangesOnly = true });
        Check(changed.Total == 1 && changed.Rungs.Single().Change == "added", "REST ladder browser filters changed rungs without losing source positions");
        var merge = new TagValueMergeRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, [new("Record", "WatchProgram", "Record", "WatchProgram"), new("Enabled", "WatchProgram", "Enabled", "WatchProgram")]);
        var preview = await Tool<TagValueMergeReport>("studio_value_merge_preview", merge);
        Check(preview.CanApply && preview.Changed == 2 && preview.Origin == "saved_project" && JsonSerializer.Serialize(preview) == JsonSerializer.Serialize(await client.PostAsync<TagValueMergeReport>("/api/value-merge/preview", merge)), "REST and MCP return the same whole-tag value merge review without connecting or mutating");
        Check((await client.GetAsync<ProjectInfo>("/api/projects/" + b.Id)).Revision == 0 && (await Call("studio_value_merge_apply", new TagValueMergeApplyRequest(merge, preview.ReviewSha256))).IsError == true, "Preview preserves destination revision and read dispatcher rejects merge apply");
        Check((await Call("studio_value_merge_apply", new TagValueMergeApplyRequest(merge, "wrong"), true)).IsError == true && (await client.GetAsync<ProjectInfo>("/api/projects/" + b.Id)).Revision == 0, "MCP merge rejects a mismatched review hash before offline mutation");
        var merged = await Tool<ProjectInfo>("studio_value_merge_apply", new TagValueMergeApplyRequest(merge, preview.ReviewSha256), true);
        var values = await client.PostAsync<StructureExtraction>("/api/structures/extract", new StructureExtractRequest(ProjectId: merged.Id, ProgramName: "WatchProgram"));
        Check(merged.Revision == 1 && merged.CurrentSha256 == preview.ResultSha256 && values.Tags.Single(t => t.Name == "Record").Value!["Count"]!.GetValue<long>() == 7 && values.Tags.Single(t => t.Name == "Enabled").Value!.GetValue<bool>(), "Reviewed MCP merge commits nested UDT and scalar values to one offline destination revision");
        var content = await client.GetAsync<ElementContent>($"/api/projects/{merged.Id}/element?xpath=" + Uri.EscapeDataString("//Tag[@Name='Record']")); var tag = XElement.Parse(content.Xml);
        Check(tag.Element("Description")!.Value == "Destination metadata" && tag.Elements("Data").Single().Attribute("Format")!.Value == "Decorated", "Value merge preserves destination metadata and removes competing stale Data representations");
        Check((await Call("studio_value_merge_apply", new TagValueMergeApplyRequest(merge, preview.ReviewSha256), true)).IsError == true && (await Call("studio_compare_routines", compare)).IsError == true, "Replayed merge and stale routine comparison are rejected after destination changes");
        var export = await client.PostAsync<Artifact>($"/api/projects/{merged.Id}/exports", new ExportRequest(merged.CurrentSha256));
        Check(ProjectService.Hash(await client.DownloadAsync(export.Id)) == merged.CurrentSha256 && (await client.GetAsync<ProjectInfo>("/api/projects/" + a.Id)).Revision == 0 && ProjectService.Hash(await File.ReadAllBytesAsync(sourcePath)) == a.SourceSha256 && ProjectService.Hash(await File.ReadAllBytesAsync(targetPath)) == b.SourceSha256, "Merged L5X exports with its reviewed hash while both original projects and the source revision remain preserved");
        var beforeJobs = (await client.GetAsync<SdkJob[]>("/api/sdk/jobs")).Length;
        Check((await Call("studio_value_merge_capture", new StructurePullRequest("unconfigured"), true)).IsError == true && (await client.GetAsync<SdkJob[]>("/api/sdk/jobs")).Length == beforeJobs, "Unconfigured online value capture creates no SDK job or connection in the isolated service");
        return checks;
    }
}
