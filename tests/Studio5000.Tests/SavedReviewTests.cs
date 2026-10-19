using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class SavedReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SavedReviews", Guid.NewGuid().ToString("N"));
    private ProjectService service;
    private ServiceOptions Options => new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] };
    public SavedReviewTests() { Directory.CreateDirectory(root); service = new(Options); }
    private async Task<ProjectInfo> Project(bool changed = false)
    {
        var doc = XmlProjects.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X")));
        var program = doc.Descendants("Program").Single(); program.Element("Routines")?.Remove();
        program.Add(new XElement("Routines", new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "NOP();")),
                new XElement("Rung", new XAttribute("Number", "1"), new XElement("Text", changed ? "XIC(Enabled)NOP();" : "NOP();"))))));
        if (changed) LogixStructures.Tag(doc, "Enabled", "WatchProgram").Descendants("DataValue").Single().SetAttributeValue("Value", "0");
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); doc.Save(path); return await service.RegisterAsync(path);
    }
    private static ProjectCompareRequest Compare(ProjectInfo a, ProjectInfo b) => new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, "Rung", IncludeRungNumbers: true);
    private static JsonElement Template(string name = "Enable mapping") => JsonSerializer.SerializeToElement(new MergeMappingTemplate(1, name, [new("Enabled", "Enabled")]), ProjectService.Json);
    [Fact]
    public async Task ComparisonPinsFiltersAndExactLadderPositionAcrossRestartsAndDetectsDrift()
    {
        var a = await Project(); var b = await Project(true); var compare = Compare(a, b);
        var r = service.CompareRoutines(compare with { Query = null }).Routines.Single();
        var bookmark = new ComparisonBookmark(r.LeftXPath, r.RightXPath, 1, RoutineQuery: "WatchProgram/Main");
        var saved = await service.SaveComparisonSessionAsync(new("Saved comparison", compare, bookmark));
        service.Dispose(); service = new(Options);
        var loaded = await service.GetComparisonSessionAsync(saved.Id);
        Assert.True(loaded.CanResume); Assert.Equal(saved, loaded.Session); Assert.Equal(bookmark, loaded.Session.Bookmark);
        Assert.Single(await service.SavedReviewsAsync("comparison-sessions"));
        var preview = await service.PreviewTagValueMergeAsync(new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, [new("Enabled", "WatchProgram", "Enabled", "WatchProgram")]));
        var updated = await service.ApplyTagValueMergeAsync(new(new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, [new("Enabled", "WatchProgram", "Enabled", "WatchProgram")]), preview.ReviewSha256));
        var stale = await service.GetComparisonSessionAsync(saved.Id); Assert.False(stale.CanResume); Assert.Single(stale.Issues);
        Assert.Equal(b.CurrentSha256, stale.Session.Comparison.RightSha256);
        await Assert.ThrowsAsync<OperationException>(() => service.SaveComparisonSessionAsync(new("Saved comparison", compare, bookmark, saved.Id, 0)));
        var rebased = await service.SaveComparisonSessionAsync(new("Explicit current comparison", compare with { RightSha256 = updated.CurrentSha256 }, bookmark, saved.Id, 0));
        Assert.Equal(1, rebased.Revision); Assert.True((await service.GetComparisonSessionAsync(saved.Id)).CanResume);
    }
    [Fact]
    public async Task MappingBindingRequiresCurrentRevisionAndFreshProjectHashesAndApplyRemainsSeparate()
    {
        var a = await Project(); var b = await Project(true); var original = File.ReadAllBytes(b.SourcePath);
        var saved = await service.SaveMergeMappingAsync(new(Template()));
        var request = new MergeMappingBindRequest(saved.Id, 0, a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, "WatchProgram", "WatchProgram");
        var binding = await service.BindMergeMappingAsync(request);
        Assert.True(binding.Preview.CanApply); Assert.Equal(b, service.Get(b.Id)); Assert.Equal(original, File.ReadAllBytes(b.SourcePath));
        service.Dispose(); service = new(Options); Assert.Equal(saved.Template.Name, (await service.GetMergeMappingAsync(saved.Id)).Template.Name);
        var edited = await service.SaveMergeMappingAsync(new(Template("New name"), saved.Id, 0)); Assert.Equal(1, edited.Revision);
        await Assert.ThrowsAsync<OperationException>(() => service.BindMergeMappingAsync(request));
        var fresh = await service.BindMergeMappingAsync(request with { ExpectedRevision = 1 });
        var merged = await service.ApplyTagValueMergeAsync(new(fresh.Proposed, fresh.Preview.ReviewSha256)); Assert.Equal(1, merged.Revision);
        await Assert.ThrowsAsync<OperationException>(() => service.BindMergeMappingAsync(request with { ExpectedRevision = 1 }));
        Assert.Equal(original, File.ReadAllBytes(b.SourcePath));
    }
    [Fact]
    public async Task MappingsRebindToExplicitDifferentProgramAndTagNames()
    {
        var a = await Project(); var b = await Project(true);
        var path = Path.Combine(root, "renamed.l5x"); var doc = XDocument.Load(b.SourcePath);
        doc.Descendants("Program").Single().SetAttributeValue("Name", "OtherProgram");
        LogixStructures.Tag(doc, "Enabled", "OtherProgram").SetAttributeValue("Name", "OtherEnabled"); doc.Save(path);
        var renamed = await service.RegisterAsync(path);
        var template = JsonSerializer.SerializeToElement(new MergeMappingTemplate(1, "Renamed pair", [new("Enabled", "OtherEnabled")]), ProjectService.Json);
        var mapping = await service.SaveMergeMappingAsync(new(template));
        var binding = await service.BindMergeMappingAsync(new(mapping.Id, 0, a.Id, a.CurrentSha256, renamed.Id, renamed.CurrentSha256, "WatchProgram", "OtherProgram"));
        Assert.True(binding.Preview.CanApply); Assert.Equal("OtherProgram", binding.Proposed.Tags![0].DestinationProgram);
        Assert.Equal("OtherEnabled", binding.Proposed.Tags[0].DestinationTag);
        Assert.DoesNotContain("ProjectId", JsonSerializer.Serialize(mapping.Template)); Assert.DoesNotContain("Program", JsonSerializer.Serialize(mapping.Template));
    }
    [Theory]
    [InlineData("{\"schemaVersion\":1,\"name\":\"A\",\"name\":\"B\",\"tags\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"A\",\"tags\":[{\"sourceTag\":\"Enabled\",\"destinationTag\":\"Enabled\",\"value\":1}]}")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"A\",\"tags\":[{\"sourceTag\":\"Enabled\",\"destinationTag\":\"Enabled\"}],\"sourcePath\":\"private\"}")]
    [InlineData("{\"schemaVersion\":2,\"name\":\"A\",\"tags\":[{\"sourceTag\":\"Enabled\",\"destinationTag\":\"Enabled\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"A\",\"tags\":[{\"sourceTag\":\"Enabled\",\"destinationTag\":\"Enabled\"},{\"sourceTag\":\"Other\",\"destinationTag\":\"enabled\"}]}")]
    public async Task InvalidPortablePayloadDoesNotCreateStoredDefinitions(string json)
    {
        await Assert.ThrowsAsync<OperationException>(() => service.SaveMergeMappingAsync(new(JsonDocument.Parse(json).RootElement)));
        Assert.Empty(await service.SavedReviewsAsync("merge-mappings"));
    }
    [Fact]
    public async Task ConcurrentUpdatesAndDeletesUseCompareAndSwapAndIdsCannotEscapeState()
    {
        var saved = await service.SaveMergeMappingAsync(new(Template()));
        async Task<bool> Update(string name) { try { await service.SaveMergeMappingAsync(new(Template(name), saved.Id, 0)); return true; } catch (OperationException e) when (e.Code == "stale_revision") { return false; } }
        Assert.Single(await Task.WhenAll(Update("One"), Update("Two")), x => x);
        await Assert.ThrowsAsync<OperationException>(() => service.DeleteSavedReviewAsync("merge-mappings", saved.Id, new(0)));
        await service.DeleteSavedReviewAsync("merge-mappings", saved.Id, new(1)); Assert.Empty(await service.SavedReviewsAsync("merge-mappings"));
        await Assert.ThrowsAsync<OperationException>(() => service.GetMergeMappingAsync("../outside"));
        Assert.Contains(service.History(), e => e.Action == "merge-mappings_delete");
    }
    [Fact]
    public async Task InvalidBookmarksAreRejectedBeforeSaving()
    {
        var a = await Project(); var b = await Project(true); var c = Compare(a, b);
        var r = service.CompareRoutines(c with { Query = null }).Routines.Single();
        await Assert.ThrowsAsync<OperationException>(() => service.SaveComparisonSessionAsync(new("Out of range", c, new(r.LeftXPath, r.RightXPath, 99))));
        await Assert.ThrowsAsync<OperationException>(() => service.SaveComparisonSessionAsync(new("Not a routine", c, new("/RSLogix5000Content/Controller", null))));
        Assert.Empty(await service.SavedReviewsAsync("comparison-sessions"));
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
