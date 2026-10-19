using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ProjectDocumentationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ProjectDocumentation", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private readonly string path;
    public ProjectDocumentationTests()
    {
        Directory.CreateDirectory(root); path = Path.Combine(root, "fixture.l5x"); File.Copy(Path.Combine(AppContext.BaseDirectory, "MultilingualDemo.L5X"), path);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] });
    }
    private static string Tag(string name) => "//Controller/Tags/Tag[@Name='" + name + "']";
    private static string Rung(int n) => "//Program[@Name='Demo']/Routines/Routine/RLLContent/Rung[@Number='" + n + "']";
    private DocumentationPage Browse(ProjectInfo p, bool missing = false) => service.BrowseDocumentation(p.Id, new(p.CurrentSha256, IncludeMissing: missing));
    private async Task<ProjectInfo> Update(ProjectInfo p, params DocumentationEdit[] edits)
    { var request = new DocumentationPreviewRequest(p.CurrentSha256, edits); var preview = await service.PreviewDocumentationAsync(p.Id, request); return await service.ApplyDocumentationAsync(p.Id, new(request, preview.ReviewSha256)); }
    private XDocument Current(ProjectInfo p) => XDocument.Parse(service.ReadElement(p.Id, "/RSLogix5000Content").Xml);
    [Fact]
    public async Task ExtractsExplicitLanguagesLegacyTextAndDescriptionsAcrossComponentScopes()
    {
        var p = await service.RegisterAsync(path); var page = Browse(p);
        Assert.Equal("en-US", page.CurrentLanguage); Assert.Contains("de-DE", page.Languages); Assert.Contains(page.Fields, f => f.OwnerKind == "Member" && f.Texts.Any(t => t.Language == "fr-FR"));
        Assert.Contains(page.Fields, f => f.OwnerKind == "Parameter"); Assert.Contains(page.Fields, f => f.Kind == "tag-comment" && f.Operand == ".Count");
        Assert.Contains(page.Fields, f => f.Name == "Enabled" && f.Texts.Single().Language is null && f.Texts.Single().Text.Contains('\n'));
        var filtered = service.BrowseDocumentation(p.Id, new(p.CurrentSha256, Kind: "rung-comment", Language: "es-ES", Limit: 1)); Assert.Equal(1, filtered.Total); Assert.Equal(2, filtered.Fields.Single().Texts.Length);
        Assert.True(Browse(p, true).Total > page.Total); Assert.Throws<OperationException>(() => service.BrowseDocumentation(p.Id, new("stale")));
    }
    [Fact]
    public async Task PreviewAndApplyPreserveOtherLanguagesLogicValuesAttributesAndOriginalBytes()
    {
        var p = await service.RegisterAsync(path); var before = Current(p); var original = File.ReadAllBytes(path);
        var request = new DocumentationPreviewRequest(p.CurrentSha256, [new(Tag("Record"), "description", "es-ES", "Producción 零\n<texto> & ]]>"), new(Rung(0), "rung-comment", "ja-JP", "出力許可"), new(Tag("Record"), "tag-comment", "de-DE", "Neue Zyklen", ".Count")]);
        var preview = await service.PreviewDocumentationAsync(p.Id, request); Assert.Equal(0, service.Get(p.Id).Revision);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyDocumentationAsync(p.Id, new(request, "wrong")));
        var next = await service.ApplyDocumentationAsync(p.Id, new(request, preview.ReviewSha256)); Assert.Equal(1, next.Revision); Assert.Equal(preview.ResultSha256, next.CurrentSha256);
        var after = Current(next); Assert.Equal(before.Descendants("Data").Select(e => e.ToString()), after.Descendants("Data").Select(e => e.ToString()));
        Assert.Equal(before.Descendants("Text").Select(e => e.Value), after.Descendants("Text").Select(e => e.Value)); Assert.Equal(before.Root!.Element("Controller")!.Attributes().Select(a => a.ToString()), after.Root!.Element("Controller")!.Attributes().Select(a => a.ToString()));
        Assert.Equal("Production record", after.Descendants("Tag").First().Element("Description")!.Elements().Single(e => (string?)e.Attribute("Lang") == "en-US").Value);
        Assert.Contains(Browse(next).Fields.SelectMany(f => f.Texts), t => t.Language == "es-ES" && t.Text == "Producción 零\n<texto> & ]]>"); Assert.Equal(original, File.ReadAllBytes(path));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyDocumentationAsync(p.Id, new(request, preview.ReviewSha256)));
    }
    [Fact]
    public async Task ExplicitLegacyPromotionRetainsOriginalAndAllowsMultipleLanguagesInOneAtomicBatch()
    {
        var p = await service.RegisterAsync(path); var edit = new DocumentationEdit(Tag("Enabled"), "description", "es-ES", "Permiso");
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewDocumentationAsync(p.Id, new(p.CurrentSha256, [edit])));
        var next = await Update(p, edit with { LegacyLanguage = "en-US" }, edit with { Language = "zh-CN", Text = "运行许可" });
        var texts = Browse(next).Fields.Single(f => f.Name == "Enabled").Texts;
        Assert.Equal(3, texts.Length); Assert.Contains(texts, t => t.Language == "en-US" && t.Text == "Legacy description\nSecond line"); Assert.Contains(texts, t => t.Language == "zh-CN");
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewDocumentationAsync(next.Id, new(next.CurrentSha256, [edit with { Language = null }])));
    }
    [Fact]
    public async Task CreatesMissingFieldsAndRemovesOnlyTheExplicitSelectedLanguage()
    {
        var p = await service.RegisterAsync(path);
        var next = await Update(p, new(Rung(1), "rung-comment", "fr-FR", "Commentaire"), new("//Program/Tags/Tag[@Name='Input']", "description", null, "Plain description"), new(Tag("Record"), "tag-comment", "fr-FR", "Bit annotation", ".Count.0"));
        next = await Update(next, new DocumentationEdit(Rung(0), "rung-comment", "es-ES", Operation: "remove"));
        var page = Browse(next); Assert.Contains(page.Fields, f => f.Kind == "tag-comment" && f.Operand == ".Count.0"); Assert.Contains(page.Fields, f => f.Name == "Input" && f.Texts.Single().Language is null);
        var rung = page.Fields.Single(f => f.Kind == "rung-comment" && f.Name == "0"); Assert.Single(rung.Texts); Assert.Equal("en-US", rung.Texts[0].Language);
    }
    [Theory]
    [InlineData("<LocalizedDescription Lang='en-US'>A</LocalizedDescription><LocalizedDescription Lang='EN-us'>B</LocalizedDescription>")]
    [InlineData("Legacy<LocalizedDescription Lang='en-US'>A</LocalizedDescription>")]
    [InlineData("<LocalizedDescription>A</LocalizedDescription>")]
    [InlineData("<FutureText>A</FutureText>")]
    public async Task AmbiguousOrUnknownDocumentationIsVisibleButCannotBeFlattenedByUpdates(string content)
    {
        var doc = XDocument.Load(path); doc.Descendants("Tag").First().Element("Description")!.ReplaceNodes(XElement.Parse("<Description>" + content + "</Description>").Nodes()); doc.Save(path);
        var p = await service.RegisterAsync(path); Assert.False(Browse(p).Fields.Single(f => f.Name == "Record" && f.Kind == "description").Writable);
        await Assert.ThrowsAsync<OperationException>(() => Update(p, new DocumentationEdit(Tag("Record"), "description", "en-US", "Replacement"))); Assert.Equal(0, service.Get(p.Id).Revision);
    }
    [Fact]
    public async Task RejectsNonDocumentationTargetsDuplicateLanguagesInvalidXmlAndUnstatedDeletions()
    {
        var p = await service.RegisterAsync(path);
        foreach (var edit in new[] { new DocumentationEdit("//Tag/Data", "description", null, "No"), new(Tag("Record"), "logic", null, "No"), new(Tag("Record"), "description", "en-US", "Bad\u0001"), new(Tag("Record"), "description", "de-DE", Operation: "remove"), new(Tag("Record"), "tag-comment", "en-US", "Bad", ".Member[Index]") })
            await Assert.ThrowsAsync<OperationException>(() => Update(p, edit));
        var one = new DocumentationEdit(Tag("Record"), "description", "en-US", "First");
        await Assert.ThrowsAsync<OperationException>(() => Update(p, one, one with { Language = "EN-us", Text = "Second" })); Assert.Equal(0, service.Get(p.Id).Revision);
    }
    [Fact]
    public async Task FullExportIgnoresPageOffsetAndExistingViewsLabelLanguagesWithoutConcatenatingThem()
    {
        var p = await service.RegisterAsync(path); var artifact = await service.ExportDocumentationAsync(p.Id, new(new(p.CurrentSha256, Kind: "description", Offset: 1, Limit: 1)));
        var bytes = File.ReadAllBytes(service.Download(artifact.Id).Path); var exported = JsonSerializer.Deserialize<DocumentationExportDocument>(bytes, ProjectService.Json)!;
        Assert.True(exported.Fields.Length > 1); Assert.Equal(p.CurrentSha256, exported.Project.Sha256); Assert.Equal(ProjectService.Hash(bytes), artifact.Sha256);
        Assert.Contains("[es-ES]", service.BrowseTags(new(p.Id, p.CurrentSha256, Query: "Record")).Nodes.Single(n => n.Name == "Record").Description);
        var ladder = service.PreviewLadder(p.Id, new(p.CurrentSha256, "//Routine[@Name='main']")); Assert.Contains("[en-US]", ladder.Rungs.First().Comment);
        Assert.Contains(service.History(p.Id), e => e.Action == "documentation_export");
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
