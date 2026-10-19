using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ComparisonReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ComparisonReviews", Guid.NewGuid().ToString("N"));
    private ProjectService service;
    private ServiceOptions Options => new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [Path.Combine(root, "exports")] };
    public ComparisonReviewTests() { Directory.CreateDirectory(root); service = new(Options); }
    private async Task<ProjectInfo> Project(int count = 1, string value = "1", string rung = "NOP();", string? description = null, int rungCount = 1)
    {
        var tags = new XElement("Tags", Enumerable.Range(0, count).Select(i => new XElement("Tag", new XAttribute("Name", "Tag" + i.ToString("D5")), new XAttribute("DataType", "DINT"),
            new XElement("DataValue", new XAttribute("Value", value)), description is null ? null : new XElement("Description", description))));
        var rungs = new XElement("RLLContent", Enumerable.Range(0, rungCount).Select(i => new XElement("Rung", new XAttribute("Number", i), new XElement("Text", rung))));
        var routine = new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"), rungs);
        var programs = new XElement("Programs", new XElement("Program", new XAttribute("Name", "Example"), new XElement("Routines", routine)));
        var doc = new XDocument(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Generated"), tags, programs)));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); doc.Save(path); return await service.RegisterAsync(path);
    }
    private static ProjectCompareRequest Compare(ProjectInfo a, ProjectInfo b, string? query = null) => new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, query);
    private ComparisonReviewDocument Document(ComparisonReviewExport exported) => JsonSerializer.Deserialize<ComparisonReviewDocument>(File.ReadAllText(service.Download(exported.Artifact.Id).Path), ProjectService.Json)!;
    [Fact]
    public async Task ExportIncludesAllMatchingChangesRegardlessOfVisiblePageAndOmitsSourcePathsAndXmlByDefault()
    {
        var a = await Project(310); var b = await Project(310, "2"); var c = Compare(a, b, "Tag") with { Offset = 300, Limit = 1 };
        Assert.Single(service.CompareProjects(c).Differences);
        var exported = await service.ExportComparisonReviewAsync(new(c, "json")); var doc = Document(exported);
        Assert.Equal(310, doc.TotalChanges); Assert.Equal(310, doc.Changes.Length); Assert.Equal("Tag", doc.Query);
        Assert.All(doc.Changes, r => { Assert.Null(r.Difference.LeftXml); Assert.Null(r.Difference.RightXml); Assert.Null(r.LeftDrawing); });
        var bytes = File.ReadAllBytes(service.Download(exported.Artifact.Id).Path); var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("sourcePath", json); Assert.DoesNotContain(root, json); Assert.Equal(ProjectService.Hash(bytes), exported.Review.Sha256);
        Assert.Equal(a, service.Get(a.Id)); Assert.Equal(b, service.Get(b.Id));
    }
    [Fact]
    public async Task HtmlEscapesAllSourceTextAndContainsOnlyGeneratedPassiveSvg()
    {
        const string payload = "</script><img src=x onerror=alert(1)>";
        var a = await Project(rung: "XIC(Start)OTE(Output);"); var b = await Project(rung: $"CUSTOM(\"{payload}\");", description: payload);
        var exported = await service.ExportComparisonReviewAsync(new(Compare(a, b), IncludeXml: true, IncludeLadder: true));
        var html = File.ReadAllText(service.Download(exported.Artifact.Id).Path);
        Assert.Contains("<!doctype html>", html); Assert.Contains("Content-Security-Policy", html); Assert.Contains("<svg", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;/script&gt;", html); Assert.DoesNotContain(a.SourcePath, html); Assert.Contains(a.CurrentSha256, html);
        var path = Path.Combine(root, "exports", "review.html"); await service.SaveArtifactAsync(exported.Artifact.Id, path);
        Assert.Equal(html, File.ReadAllText(path)); await Assert.ThrowsAsync<OperationException>(() => service.SaveArtifactAsync(exported.Artifact.Id, path));
    }
    [Fact]
    public async Task InvalidLadderRetainsDiagnosticsAndLargeXmlRetainsTruncationFlags()
    {
        var a = await Project(description: new string('a', 5000)); var b = await Project(rung: "incomplete(", description: new string('b', 5000));
        var exported = await service.ExportComparisonReviewAsync(new(Compare(a, b), "json", true, true)); var doc = Document(exported);
        var tag = doc.Changes.Single(c => c.Difference.Kind == "Tag").Difference;
        Assert.True(tag.LeftTruncated); Assert.True(tag.RightTruncated); Assert.Equal(4096, tag.LeftXml!.Length);
        var rung = doc.Changes.Single(c => c.Difference.Kind == "Rung"); Assert.Equal("invalid", rung.RightDrawing!.Status); Assert.Null(rung.RightDrawing.Svg); Assert.NotEmpty(rung.RightDrawing.Diagnostics);
    }
    [Fact]
    public async Task HistorySurvivesRestartAndProjectChangesAndForgettingPreservesTheArtifact()
    {
        var a = await Project(); var b = await Project(value: "2"); var exported = await service.ExportComparisonReviewAsync(new(Compare(a, b), "json"));
        var draft = await service.PreviewAsync(b.Id, new(b.CurrentSha256, [new("set_attribute", "/RSLogix5000Content/Controller/Tags/Tag/DataValue", "3", "Value")]));
        var changed = await service.ApplyAsync(b.Id, draft.Id, new(b.CurrentSha256));
        service.Dispose(); service = new(Options);
        Assert.Equal(exported.Review, await service.ComparisonReviewAsync(exported.Review.Id)); Assert.NotEqual(changed.CurrentSha256, exported.Review.Right.Sha256);
        Assert.Single((await service.ComparisonReviewsAsync()).Reviews);
        await Assert.ThrowsAsync<OperationException>(() => service.ForgetComparisonReviewAsync(exported.Review.Id, new("wrong")));
        await service.ForgetComparisonReviewAsync(exported.Review.Id, new(exported.Review.Sha256)); Assert.Empty((await service.ComparisonReviewsAsync()).Reviews);
        Assert.True(File.Exists(service.Download(exported.Artifact.Id).Path)); Assert.Equal(changed, service.Get(b.Id));
        Assert.Contains(service.History(), h => h.Action == "comparison-reviews_forget");
    }
    [Fact]
    public async Task HistoryIsPagedSearchableAndReadOnlyUntilExport()
    {
        Assert.Empty((await service.ComparisonReviewsAsync()).Reviews); Assert.False(Directory.Exists(Path.Combine(root, "state", "comparison-reviews")));
        var a = await Project(); var b = await Project(value: "2");
        var first = await service.ExportComparisonReviewAsync(new(Compare(a, b), "json"));
        var second = await service.ExportComparisonReviewAsync(new(Compare(a, b, "Tag"), "json"));
        var page = await service.ComparisonReviewsAsync(limit: 1); var next = await service.ComparisonReviewsAsync(offset: 1, limit: 1);
        Assert.Equal(2, page.Total); Assert.NotEqual(page.Reviews.Single().Id, next.Reviews.Single().Id);
        Assert.Equal(second.Review.Id, (await service.ComparisonReviewsAsync(query: second.Review.Id)).Reviews.Single().Id);
        Assert.Empty((await service.ComparisonReviewsAsync("no-such-review")).Reviews);
        await Assert.ThrowsAsync<OperationException>(() => service.ComparisonReviewsAsync(limit: 101));
        await Assert.ThrowsAsync<OperationException>(() => service.ComparisonReviewAsync("../escape"));
    }
    [Fact]
    public async Task TamperedReportArtifactsAreRejectedIndependentlyOfProjectState()
    {
        var a = await Project(); var b = await Project(value: "2"); var exported = await service.ExportComparisonReviewAsync(new(Compare(a, b), "json"));
        File.AppendAllText(service.Download(exported.Artifact.Id).Path, "tampered");
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ComparisonReviewAsync(exported.Review.Id)); Assert.Equal("artifact_tampered", error.Code);
        Assert.Equal(0, service.Get(a.Id).Revision); Assert.Equal(0, service.Get(b.Id).Revision);
    }
    [Theory]
    [InlineData("csv", false)]
    [InlineData("html", true)]
    public async Task InvalidFormatsAndStaleHashesLeaveNoReportOrHistoryEntry(string format, bool stale)
    {
        var a = await Project(); var b = await Project(value: "2"); var c = Compare(a, b); if (stale) c = c with { RightSha256 = "wrong" };
        await Assert.ThrowsAsync<OperationException>(() => service.ExportComparisonReviewAsync(new(c, format)));
        Assert.Empty((await service.ComparisonReviewsAsync()).Reviews); Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "state", "artifacts")));
    }
    [Fact]
    public async Task ChangeAndDrawingBudgetsRejectWholeExportRatherThanTruncatingIt()
    {
        var a = await Project(5001); var b = await Project(5001, "2");
        await Assert.ThrowsAsync<OperationException>(() => service.ExportComparisonReviewAsync(new(Compare(a, b), "json")));
        Assert.Empty((await service.ComparisonReviewsAsync()).Reviews);
        var narrow = await service.ExportComparisonReviewAsync(new(Compare(a, b, "Tag00000"), "json")); Assert.Single(Document(narrow).Changes);
        var c = await Project(rungCount: 201); var d = await Project(rung: "XIC(Start)NOP();", rungCount: 201);
        await Assert.ThrowsAsync<OperationException>(() => service.ExportComparisonReviewAsync(new(Compare(c, d), "json", IncludeLadder: true)));
        var noDrawings = await service.ExportComparisonReviewAsync(new(Compare(c, d), "json")); Assert.Equal(201, Document(noDrawings).Changes.Length);
    }
    [Fact]
    public async Task ByteLimitRejectsLargeContentBeforePublishingAndContentCanBeOmitted()
    {
        var a = await Project(1300, description: new string('<', 1100)); var b = await Project(1300, "2", description: new string('>', 1100));
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ExportComparisonReviewAsync(new(Compare(a, b), "json", true)));
        Assert.Equal("comparison_review_limit", error.Code); Assert.Empty((await service.ComparisonReviewsAsync()).Reviews);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "state", "artifacts")));
        var compact = await service.ExportComparisonReviewAsync(new(Compare(a, b), "json")); Assert.Equal(1300, Document(compact).Changes.Length);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
