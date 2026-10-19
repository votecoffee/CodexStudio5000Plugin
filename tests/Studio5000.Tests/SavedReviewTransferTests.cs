using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class SavedReviewTransferTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ReviewTransfers", Guid.NewGuid().ToString("N"));
    private ProjectService service;
    private ServiceOptions Options => new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [Path.Combine(root, "exports")] };
    public SavedReviewTransferTests() { Directory.CreateDirectory(root); service = new(Options); }
    private static JsonElement Template => JsonSerializer.SerializeToElement(new MergeMappingTemplate(1, "Portable pairs", [new("Enabled", "Enabled")]), ProjectService.Json);
    private async Task<ProjectInfo> Project(bool changed = false)
    {
        var doc = XmlProjects.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X")));
        var program = doc.Descendants("Program").Single(); program.Element("Routines")?.Remove();
        program.Add(new XElement("Routines", new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "NOP();"))))));
        if (changed) LogixStructures.Tag(doc, "Enabled", "WatchProgram").Descendants("DataValue").Single().SetAttributeValue("Value", "0");
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); doc.Save(path); return await service.RegisterAsync(path);
    }
    private async Task<(ProjectInfo Left, ProjectInfo Right, ComparisonSession Session, ComparisonSessionRecoveryRequest Recovery)> Session()
    {
        var a = await Project(); var b = await Project(true); var compare = new ProjectCompareRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256);
        var r = service.CompareRoutines(compare).Routines.Single();
        var saved = await service.SaveComparisonSessionAsync(new("Pinned review", compare, new(r.LeftXPath, r.RightXPath)));
        return (a, b, saved, new(saved.Id, 0, compare with { Query = "Tag", Offset = 2 }));
    }
    [Fact]
    public async Task PortableMappingRoundTripPreservesOnlyNamesAndCreatesNewDefinitions()
    {
        var original = await service.SaveMergeMappingAsync(new(Template));
        var artifact = await service.ExportMergeMappingAsync(original.Id, new(0));
        var download = service.Download(artifact.Id); var bytes = File.ReadAllBytes(download.Path);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(new[] { "schemaVersion", "name", "tags" }, document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(ProjectService.Hash(bytes), artifact.Sha256);
        var imported = await service.ImportMergeMappingAsync(new(document.RootElement));
        Assert.NotEqual(original.Id, imported.Id); Assert.Equal(0, imported.Revision); Assert.Equal(original.Template.Tags, imported.Template.Tags);
        Assert.Equal(original.Template.Name, imported.Template.Name); Assert.Equal(0, (await service.GetMergeMappingAsync(original.Id)).Revision);
        var path = Path.Combine(root, "exports", "mapping.json"); await service.SaveArtifactAsync(artifact.Id, path);
        await Assert.ThrowsAsync<OperationException>(() => service.SaveArtifactAsync(artifact.Id, path));
        await Assert.ThrowsAsync<OperationException>(() => service.SaveArtifactAsync(artifact.Id, Path.Combine(root, "outside.json")));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        service.Dispose(); service = new(Options); Assert.Equal(2, (await service.SavedReviewsAsync("merge-mappings")).Length);
    }
    [Fact]
    public async Task StaleMappingExportCannotPublishAnOutdatedDefinition()
    {
        var saved = await service.SaveMergeMappingAsync(new(Template));
        await service.SaveMergeMappingAsync(new(Template, saved.Id, 0));
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ExportMergeMappingAsync(saved.Id, new(0)));
        Assert.Equal("stale_revision", error.Code); Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "state", "artifacts")));
    }
    [Theory]
    [InlineData("{\"schemaVersion\":1,\"name\":\"A\",\"tags\":[],\"sourceProjectId\":\"private\"}")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"A\",\"tags\":[{\"sourceTag\":\"Enabled\",\"destinationTag\":\"Enabled\",\"value\":5}]}")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"A\",\"tags\":[],\"name\":\"B\"}")]
    public async Task ImportRejectsNonportableOrAmbiguousFieldsWithoutSaving(string json)
    {
        using var document = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<OperationException>(() => service.ImportMergeMappingAsync(new(document.RootElement)));
        Assert.Empty(await service.SavedReviewsAsync("merge-mappings"));
    }
    [Fact]
    public void ExportableCanonicalMappingsAlsoMeetTheImportSizeLimit()
    {
        var oversized = new MergeMappingTemplate(1, "Too large", [new(new string('A', MergeMappingTemplates.MaximumBytes), "Enabled")]);
        Assert.Throws<OperationException>(() => MergeMappingTemplates.Bytes(oversized));
    }
    [Fact]
    public async Task RecoveryPreviewIsReadOnlyAndApplyClearsOldBookmarkAndPagination()
    {
        var (a, b, saved, request) = await Session(); var preview = await service.PreviewComparisonRecoveryAsync(request);
        Assert.Equal(saved, (await service.GetComparisonSessionAsync(saved.Id)).Session); Assert.Equal(0, preview.Proposed.Offset);
        var recovered = await service.ApplyComparisonRecoveryAsync(new(request, preview.ReviewSha256));
        Assert.Equal(1, recovered.Revision); Assert.Equal(saved.Name, recovered.Name); Assert.Null(recovered.Bookmark); Assert.Equal("Tag", recovered.Comparison.Query);
        Assert.Equal(0, recovered.Comparison.Offset); Assert.Equal(a, service.Get(a.Id)); Assert.Equal(b, service.Get(b.Id));
        Assert.Equal(a.SourceSha256, ProjectService.Hash(File.ReadAllBytes(a.SourcePath))); Assert.Equal(b.SourceSha256, ProjectService.Hash(File.ReadAllBytes(b.SourcePath)));
        service.Dispose(); service = new(Options); Assert.Equal(recovered, (await service.GetComparisonSessionAsync(saved.Id)).Session);
    }
    [Fact]
    public async Task RecoveryRequiresExactReviewedFiltersAndSessionRevision()
    {
        var (_, _, saved, request) = await Session(); var preview = await service.PreviewComparisonRecoveryAsync(request);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyComparisonRecoveryAsync(new(request, "wrong")));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyComparisonRecoveryAsync(new(request with { Comparison = request.Comparison with { Query = "Rung" } }, preview.ReviewSha256)));
        Assert.Equal(saved, (await service.GetComparisonSessionAsync(saved.Id)).Session);
        await service.SaveComparisonSessionAsync(new("Another client", saved.Comparison, saved.Bookmark, saved.Id, 0));
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ApplyComparisonRecoveryAsync(new(request, preview.ReviewSha256)));
        Assert.Equal("stale_revision", error.Code); Assert.Equal("Another client", (await service.GetComparisonSessionAsync(saved.Id)).Session.Name);
    }
    [Fact]
    public async Task RecoveryRechecksDestinationHashAfterAnOfflineProjectMutation()
    {
        var (a, b, saved, request) = await Session(); var preview = await service.PreviewComparisonRecoveryAsync(request);
        var merge = new TagValueMergeRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, [new("Enabled", "WatchProgram", "Enabled", "WatchProgram")]);
        var values = await service.PreviewTagValueMergeAsync(merge); var updated = await service.ApplyTagValueMergeAsync(new(merge, values.ReviewSha256));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyComparisonRecoveryAsync(new(request, preview.ReviewSha256)));
        var stale = await service.GetComparisonSessionAsync(saved.Id); Assert.False(stale.CanResume); Assert.Equal(saved, stale.Session);
        var current = request with { Comparison = request.Comparison with { RightSha256 = updated.CurrentSha256 } };
        var fresh = await service.PreviewComparisonRecoveryAsync(current); var recovered = await service.ApplyComparisonRecoveryAsync(new(current, fresh.ReviewSha256));
        Assert.Equal(updated.CurrentSha256, recovered.Comparison.RightSha256); Assert.True((await service.GetComparisonSessionAsync(saved.Id)).CanResume);
    }
    [Fact]
    public async Task ConcurrentRecoveryAppliesCannotOverwriteEachOther()
    {
        var (_, _, saved, request) = await Session(); var preview = await service.PreviewComparisonRecoveryAsync(request);
        async Task<bool> Apply() { try { await service.ApplyComparisonRecoveryAsync(new(request, preview.ReviewSha256)); return true; } catch (OperationException e) when (e.Code == "stale_revision") { return false; } }
        Assert.Single(await Task.WhenAll(Apply(), Apply()), result => result);
        Assert.Equal(1, (await service.GetComparisonSessionAsync(saved.Id)).Session.Revision);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
