using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;

internal static class ViewDesignerIntegration
{
    public static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        var source = Path.Combine(root, "examples", "ViewDesignerDemo");
        var screen = "User-Defined Screens/Overview.hmi";
        var before = ProjectService.Hash(File.ReadAllBytes(Path.Combine(source, screen)));
        var result = await mcp.CallToolAsync("viewdesigner_register_export", new Dictionary<string, object?> { ["path"] = source });
        Check(result.IsError != true, "View Designer MCP registers a generated exported folder");
        var p = JsonSerializer.Deserialize<ProjectInfo>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        var inspected = await client.GetAsync<ViewDesignerInspection>($"/api/viewdesigner/projects/{p.Id}");
        Check(inspected.Files.Length == 7 && inspected.Files.Count(f => f.Editable) == 6, "View Designer REST sees HMI documents and preserved image asset");
        var doc = await client.GetAsync<ViewDesignerContent>($"/api/viewdesigner/projects/{p.Id}/file?file={Uri.EscapeDataString(screen)}");
        Check(doc.Document.Objects.Any(x => x.Type == "DisplayPopupCommand") && doc.Document.Properties.Any(x => x.Selector.EndsWith("/parameter:Title")), "View Designer reads nested command parameters");
        var edits = new[] { new ViewDesignerEdit(screen, "/Screen:Overview/TextDisplay:Status/@Text", "\"API verified 零\""),
            new ViewDesignerEdit(screen, "/Screen:Overview/NumericDisplay:ProcessValue/@Value", "0 -> \"::LGX.Other.Count\"") };
        var draft = await client.PostAsync<ViewDesignerPreview>($"/api/viewdesigner/projects/{p.Id}/previews", new ViewDesignerPreviewRequest(p.CurrentSha256, edits));
        Check((await client.GetAsync<ViewDesignerInspection>($"/api/viewdesigner/projects/{p.Id}")).Project.Revision == 0, "View Designer preview leaves current revision unchanged");
        result = await mcp.CallToolAsync("viewdesigner_apply_changes", new Dictionary<string, object?> { ["projectId"] = p.Id, ["previewId"] = draft.Id, ["expectedSha256"] = p.CurrentSha256 });
        Check(result.IsError != true, "View Designer MCP applies REST preview through the shared service");
        p = (await client.GetAsync<ViewDesignerInspection>($"/api/viewdesigner/projects/{p.Id}")).Project;
        var plan = await client.PostAsync<ViewDesignerImportPlan>($"/api/viewdesigner/projects/{p.Id}/imports", new ViewDesignerImportRequest(p.CurrentSha256, "Synthetic validation target", "Same PanelView 5510", "en-US"));
        Check(plan.Changes.Length == 2 && plan.NativeImportStatus == "pending", "View Designer import package binds exact changes and leaves native acceptance pending");
        Check(ProjectService.Hash(await client.DownloadAsync(plan.Package.Id)) == plan.Package.Sha256, "View Designer ZIP download has the reviewed hash");
        var tree = Path.GetDirectoryName(plan.ImportEntryPath)!;
        foreach (var f in Directory.GetFiles(tree, "*", SearchOption.AllDirectories))
        { var dest = Path.Combine(plan.ReexportDirectory, Path.GetRelativePath(tree, f)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(f, dest); }
        result = await mcp.CallToolAsync("viewdesigner_verify_import", new Dictionary<string, object?> { ["planId"] = plan.Id });
        var receipt = JsonSerializer.Deserialize<ViewDesignerVerification>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        Check(receipt.Status == "property_values_match" && receipt.NativeImportStatus == "not_observed_by_service", "View Designer simulated re-export comparison does not claim native validation");
        Check(ProjectService.Hash(File.ReadAllBytes(Path.Combine(source, screen))) == before, "View Designer API edits preserve source export bytes");
        return checks;
    }
}
