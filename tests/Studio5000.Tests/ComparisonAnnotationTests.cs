using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ComparisonAnnotationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ComparisonAnnotations", Guid.NewGuid().ToString("N"));
    private ProjectService service;
    private ServiceOptions Options => new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] };
    public ComparisonAnnotationTests() { Directory.CreateDirectory(root); service = new(Options); }
    private async Task<(ProjectInfo Left, ProjectInfo Right, ComparisonReviewExport Export)> Report(string format = "html", int count = 3)
    {
        var tags = new XElement("Tags", Enumerable.Range(0, count).Select(i => new XElement("Tag", new XAttribute("Name", "Value" + i.ToString("D4")),
            new XAttribute("DataType", "DINT"), new XElement("DataValue", new XAttribute("Value", "1")))));
        var doc = new XDocument(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Generated"), tags)));
        var aPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); var bPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); doc.Save(aPath);
        foreach (var value in doc.Descendants("DataValue")) value.SetAttributeValue("Value", "2"); doc.Save(bPath);
        var a = await service.RegisterAsync(aPath); var b = await service.RegisterAsync(bPath);
        return (a, b, await service.ExportComparisonReviewAsync(new(new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256), format)));
    }
    private Task<ComparisonAnnotationSummary> Save(ComparisonReviewExport report, int revision, params ComparisonAnnotation[] notes) =>
        service.UpdateComparisonAnnotationsAsync(report.Review.Id, new(report.Artifact.Sha256, revision, notes));
    private string StatePath(ComparisonReviewExport report) => Path.Combine(root, "state", "comparison-annotations", report.Review.Id + ".json");
    private void Legacy(ComparisonReviewExport report)
    {
        var path = Path.Combine(root, "state", "comparison-reviews", report.Review.Id + ".json");
        var node = JsonNode.Parse(File.ReadAllText(path))!; node.AsObject().Remove("catalogSha256"); File.WriteAllText(path, node.ToJsonString(ProjectService.Json));
        File.Delete(Path.Combine(root, "state", "artifacts", report.Artifact.Id, "changes.json"));
    }
    [Fact]
    public async Task CatalogReadsArePagedStableAndCreateNoAnnotationStateAcrossRestarts()
    {
        var (_, _, report) = await Report(count: 205); var first = await service.ComparisonAnnotationsAsync(report.Review.Id, limit: 100);
        var next = await service.ComparisonAnnotationsAsync(report.Review.Id, offset: 100, limit: 100);
        Assert.Equal(205, first.Summary.Unreviewed); Assert.Equal(0, first.Summary.Revision); Assert.Equal(100, next.Changes[0].Change.Ordinal);
        Assert.Empty(first.Changes.Select(c => c.Change.Id).Intersect(next.Changes.Select(c => c.Change.Id)));
        Assert.False(Directory.Exists(Path.Combine(root, "state", "comparison-annotations")));
        service.Dispose(); service = new(Options); var loaded = await service.ComparisonAnnotationsAsync(report.Review.Id, limit: 100);
        Assert.Equal(first.Changes.Select(c => c.Change.Id), loaded.Changes.Select(c => c.Change.Id));
        Assert.Equal(report.Artifact.Sha256, loaded.Summary.ReportSha256);
    }
    [Fact]
    public async Task NotesStatusesAndProgressPersistAndDefaultResetRemovesOnlyThatAnnotation()
    {
        var (_, _, report) = await Report(); var page = await service.ComparisonAnnotationsAsync(report.Review.Id);
        var a = page.Changes[0].Change.Id; var b = page.Changes[1].Change.Id;
        var saved = await Save(report, 0, new(a, "reviewed", "Checked the stored value."), new(b, "needs-attention", "Check range\nwith the project owner."));
        Assert.Equal(1, saved.Reviewed); Assert.Equal(1, saved.NeedsAttention); Assert.Equal(1, saved.Unreviewed);
        service.Dispose(); service = new(Options);
        var attention = await service.ComparisonAnnotationsAsync(report.Review.Id, query: "range", status: "needs-attention");
        Assert.Single(attention.Changes); Assert.Equal(b, attention.Changes[0].Change.Id); Assert.Equal(3, attention.Summary.Total);
        var reset = await Save(report, 1, new ComparisonAnnotation(a, "unreviewed", "")); Assert.Equal(2, reset.Revision); Assert.Equal(2, reset.Unreviewed);
        Assert.Equal("Check range\nwith the project owner.", (await service.ComparisonAnnotationsAsync(report.Review.Id, status: "needs-attention")).Changes.Single().Note);
        Assert.Single(JsonSerializer.Deserialize<ComparisonAnnotationState>(File.ReadAllText(StatePath(report)), ProjectService.Json)!.Items);
    }
    [Fact]
    public async Task ConcurrentSavesUseLoadedRevisionAndBatchFailuresAreAtomic()
    {
        var (_, _, report) = await Report(); var page = await service.ComparisonAnnotationsAsync(report.Review.Id); var id = page.Changes[0].Change.Id;
        await Assert.ThrowsAsync<OperationException>(() => Save(report, 0, new(id, "reviewed", "Valid first item"), new("unknown", "reviewed", "Invalid second item")));
        Assert.Equal(0, (await service.ComparisonAnnotationsAsync(report.Review.Id)).Summary.Revision); Assert.False(File.Exists(StatePath(report)));
        async Task<bool> Write(string note) { try { await Save(report, 0, new ComparisonAnnotation(id, "reviewed", note)); return true; } catch (OperationException e) when (e.Code == "stale_revision") { return false; } }
        Assert.Single(await Task.WhenAll(Write("First client"), Write("Second client")), result => result);
        Assert.Equal(1, (await service.ComparisonAnnotationsAsync(report.Review.Id)).Summary.Revision);
    }
    [Fact]
    public async Task AnotherReportCannotReuseChangeIdsOrExpectedHashesEvenForTheSameSnapshots()
    {
        var (a, b, first) = await Report(); var second = await service.ExportComparisonReviewAsync(new(new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256)));
        var one = (await service.ComparisonAnnotationsAsync(first.Review.Id)).Changes[0].Change;
        var two = (await service.ComparisonAnnotationsAsync(second.Review.Id)).Changes[0].Change;
        Assert.NotEqual(one.Id, two.Id);
        await Assert.ThrowsAsync<OperationException>(() => Save(second, 0, new ComparisonAnnotation(one.Id, "reviewed", "Wrong report")));
        await Assert.ThrowsAsync<OperationException>(() => service.UpdateComparisonAnnotationsAsync(second.Review.Id, new(first.Artifact.Sha256, 0, [new(two.Id, "reviewed", "Wrong hash")])));
        Assert.False(File.Exists(StatePath(second)));
    }
    [Theory]
    [InlineData("approved", "Text")]
    [InlineData("reviewed", null)]
    [InlineData("reviewed", "bad\0note")]
    public async Task InvalidNotesOrStatusesNeverChangeProgress(string status, string? note)
    {
        var (_, _, report) = await Report(); var id = (await service.ComparisonAnnotationsAsync(report.Review.Id)).Changes[0].Change.Id;
        await Assert.ThrowsAsync<OperationException>(() => Save(report, 0, new ComparisonAnnotation(id, status, note!)));
        Assert.Equal(0, (await service.ComparisonAnnotationsAsync(report.Review.Id)).Summary.Revision);
    }
    [Fact]
    public async Task DuplicateChangesAndOversizedNotesOrBatchesAreRejected()
    {
        var (_, _, report) = await Report(); var id = (await service.ComparisonAnnotationsAsync(report.Review.Id)).Changes[0].Change.Id;
        await Assert.ThrowsAsync<OperationException>(() => Save(report, 0, new(id, "reviewed", ""), new(id.ToUpperInvariant(), "reviewed", "")));
        await Assert.ThrowsAsync<OperationException>(() => Save(report, 0, new ComparisonAnnotation(id, "reviewed", new string('x', 2001))));
        await Assert.ThrowsAsync<OperationException>(() => Save(report, 0, Enumerable.Repeat(new ComparisonAnnotation(id, "reviewed", ""), 101).ToArray()));
        await Assert.ThrowsAsync<OperationException>(() => service.ComparisonAnnotationsAsync(report.Review.Id, limit: 101));
        await Assert.ThrowsAsync<OperationException>(() => service.ComparisonAnnotationsAsync("../escape"));
        Assert.False(File.Exists(StatePath(report)));
    }
    [Fact]
    public async Task TotalStateByteBudgetRejectsTheWholeBatchAndRetainsPreviousProgress()
    {
        var (_, _, report) = await Report(count: 200); var first = await service.ComparisonAnnotationsAsync(report.Review.Id, limit: 100);
        var next = await service.ComparisonAnnotationsAsync(report.Review.Id, offset: 100, limit: 100); var note = new string('ü', 2000);
        await Save(report, 0, first.Changes.Select(c => new ComparisonAnnotation(c.Change.Id, "reviewed", note)).ToArray());
        var error = await Assert.ThrowsAsync<OperationException>(() => Save(report, 1, next.Changes.Select(c => new ComparisonAnnotation(c.Change.Id, "reviewed", note)).ToArray()));
        Assert.Equal("comparison_annotation_limit", error.Code);
        var after = await service.ComparisonAnnotationsAsync(report.Review.Id); Assert.Equal(1, after.Summary.Revision); Assert.Equal(100, after.Summary.Reviewed); Assert.Equal(100, after.Summary.Unreviewed);
    }
    [Fact]
    public async Task NotesExportRechecksRevisionAndPreservesReportBytesAndProjectOriginals()
    {
        var (a, b, report) = await Report(); var bytes = File.ReadAllBytes(service.Download(report.Artifact.Id).Path);
        var page = await service.ComparisonAnnotationsAsync(report.Review.Id); await Save(report, 0, new ComparisonAnnotation(page.Changes[0].Change.Id, "reviewed", "Recorded note"));
        await Assert.ThrowsAsync<OperationException>(() => service.ExportComparisonAnnotationsAsync(report.Review.Id, new(report.Artifact.Sha256, 0)));
        var exported = await service.ExportComparisonAnnotationsAsync(report.Review.Id, new(report.Artifact.Sha256, 1));
        var json = File.ReadAllText(service.Download(exported.Id).Path); var document = JsonSerializer.Deserialize<ComparisonAnnotationExportDocument>(json, ProjectService.Json)!;
        Assert.Equal(1, document.Summary.Revision); Assert.Equal(report.Artifact.Sha256, document.Report.Sha256); Assert.Equal(0, Assert.Single(document.Notes).Change.Ordinal);
        Assert.DoesNotContain("sourcePath", json); Assert.DoesNotContain(root, json); Assert.Equal(bytes, File.ReadAllBytes(service.Download(report.Artifact.Id).Path));
        Assert.Equal(a.SourceSha256, ProjectService.Hash(File.ReadAllBytes(a.SourcePath))); Assert.Equal(b.SourceSha256, ProjectService.Hash(File.ReadAllBytes(b.SourcePath)));
    }
    [Fact]
    public async Task AnnotationsRemainBoundToTheRecordedReportAfterProjectChanges()
    {
        var (_, b, report) = await Report(); var page = await service.ComparisonAnnotationsAsync(report.Review.Id); await Save(report, 0, new ComparisonAnnotation(page.Changes[0].Change.Id, "needs-attention", "Recorded against this report"));
        var draft = await service.PreviewAsync(b.Id, new(b.CurrentSha256, [new("set_attribute", "/RSLogix5000Content/Controller/Tags/Tag[@Name='Value0000']/DataValue", "3", "Value")]));
        await service.ApplyAsync(b.Id, draft.Id, new(b.CurrentSha256));
        var retained = await service.ComparisonAnnotationsAsync(report.Review.Id); Assert.Equal(1, retained.Summary.NeedsAttention); Assert.Equal(report.Artifact.Sha256, retained.Summary.ReportSha256);
    }
    [Fact]
    public async Task ReportCatalogAndAnnotationStateTamperingAreRejectedBeforeSaving()
    {
        var (_, _, report) = await Report(); var page = await service.ComparisonAnnotationsAsync(report.Review.Id); var row = page.Changes[0].Change.Id;
        var catalog = Path.Combine(root, "state", "artifacts", report.Artifact.Id, "changes.json"); var original = File.ReadAllBytes(catalog); File.AppendAllText(catalog, "tampered");
        await Assert.ThrowsAsync<OperationException>(() => Save(report, 0, new ComparisonAnnotation(row, "reviewed", ""))); Assert.False(File.Exists(StatePath(report)));
        File.WriteAllBytes(catalog, original); await Save(report, 0, new ComparisonAnnotation(row, "reviewed", ""));
        var state = JsonNode.Parse(File.ReadAllText(StatePath(report)))!; state["reportSha256"] = "wrong"; File.WriteAllText(StatePath(report), state.ToJsonString());
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ComparisonAnnotationsAsync(report.Review.Id)); Assert.Equal("state_tampered", error.Code);
    }
    [Fact]
    public async Task LegacyJsonUsesItsRecordedChangesAndLegacyHtmlRequiresANewReport()
    {
        var (_, _, json) = await Report("json"); Legacy(json);
        Assert.Equal(3, (await service.ComparisonAnnotationsAsync(json.Review.Id)).Summary.Total); Assert.False(File.Exists(StatePath(json)));
        var (_, _, html) = await Report(); Legacy(html);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ComparisonAnnotationsAsync(html.Review.Id));
        Assert.Equal("comparison_annotations_unavailable", error.Code); Assert.False(File.Exists(StatePath(html)));
    }
    [Fact]
    public async Task ForgettingHistoryPreservesLocalNotesAndZeroChangeReportsRemainEmpty()
    {
        var (a, _, report) = await Report(); var row = (await service.ComparisonAnnotationsAsync(report.Review.Id)).Changes[0].Change.Id;
        await Save(report, 0, new ComparisonAnnotation(row, "reviewed", "Keep note")); await service.ForgetComparisonReviewAsync(report.Review.Id, new(report.Artifact.Sha256));
        Assert.True(File.Exists(StatePath(report))); Assert.True(File.Exists(service.Download(report.Artifact.Id).Path));
        var empty = await service.ExportComparisonReviewAsync(new(new(a.Id, a.CurrentSha256, a.Id, a.CurrentSha256)));
        var page = await service.ComparisonAnnotationsAsync(empty.Review.Id); Assert.Equal(0, page.Summary.Total); Assert.Empty(page.Changes);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
