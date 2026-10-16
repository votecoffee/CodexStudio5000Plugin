using System.Text.Json.Nodes;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class BrowserRecordingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BrowserRecording", Guid.NewGuid().ToString("N"));
    private readonly ServiceOptions options;
    private readonly Reader reader = new();
    private ProjectService service;
    private readonly string source;
    public BrowserRecordingTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "fixture.l5x"); File.Copy(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"), source);
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [Path.Combine(root, "exports")] };
        service = new(options, reader);
    }
    private async Task<WatchStatus> Create()
    {
        var project = await service.RegisterAsync(source);
        return await service.CreateBrowserWatchAsync(new(project.Id, project.CurrentSha256, "=Test, recorder", [new("count", "Record", "WatchProgram", "Inner.Count")]));
    }
    private static async Task Until(Func<bool> test)
    { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!test()) await Task.Delay(15, ct.Token); }
    [Fact]
    public async Task BrowserSearchesScopesAndTypesAndLazilyPagesArrayMembers()
    {
        var project = await service.RegisterAsync(source);
        var page = service.BrowseTags(new(project.Id, Query: "watchrecord"));
        Assert.Equal(2, page.Nodes.Length); Assert.Contains("WatchProgram", page.Programs); Assert.Equal(2, page.Types.Length);
        var members = service.BrowseTags(new(project.Id, page.SourceSha256, "WatchProgram", "Record"));
        Assert.Contains(members.Nodes, n => n.Name == "Inner" && n.Expandable);
        var array = service.BrowseTags(new(project.Id, page.SourceSha256, "WatchProgram", "Record", "Samples", Offset: 1, Limit: 1));
        Assert.Equal(3, array.Total); Assert.Equal("Samples[1]", Assert.Single(array.Nodes).MemberPath);
        var leaf = service.BrowseTags(new(project.Id, page.SourceSha256, "WatchProgram", "Records", "[1].Inner.Count"));
        Assert.Empty(leaf.Nodes); Assert.Equal("DINT", leaf.Selection!.DataType); Assert.True(leaf.Selection.Watchable);
        Assert.Throws<OperationException>(() => service.BrowseTags(new(project.Id, TagName: "Records", ProgramName: "WatchProgram", MemberPath: "[2]")));
        Assert.Throws<OperationException>(() => service.BrowseTags(new(project.Id, new string('0', 64))));
        Assert.Equal(0, reader.Opens);
    }
    [Fact]
    public async Task SchemaBrowserDoesNotDecodeHugeArraysOrDependOnDecoratedValues()
    {
        var doc = XDocument.Load(source); var tag = doc.Descendants("Tag").First();
        tag.SetAttributeValue("Dimensions", "1000000"); tag.Elements("Data").Remove(); doc.Save(source);
        var project = await service.RegisterAsync(source);
        var result = service.BrowseTags(new(project.Id, ProgramName: "WatchProgram", TagName: (string)tag.Attribute("Name")!, Offset: 999999, Limit: 1));
        Assert.Equal(1000000, result.Total); Assert.Equal("[999999]", Assert.Single(result.Nodes).MemberPath);
    }
    [Fact]
    public async Task BrowserWatchPreservesItsSourceAndRejectsAliasesAndHiddenMembers()
    {
        var watch = await Create(); Assert.Equal("stopped", watch.State); Assert.NotNull(watch.Definition.Settings.SourceArtifactId); Assert.Equal(0, reader.Opens);
        var doc = XDocument.Load(source); var member = doc.Descendants("Member").First(m => (string?)m.Attribute("Name") == "Count");
        member.SetAttributeValue("Hidden", "true"); doc.Save(source); var project = await service.RegisterAsync(source);
        await Assert.ThrowsAsync<OperationException>(() => service.CreateBrowserWatchAsync(new(project.Id, project.CurrentSha256, "bad", [new("x", "Record", "WatchProgram", "Inner.Count")])));
        doc.Descendants("Tag").First(t => (string?)t.Attribute("Name") == "Record").SetAttributeValue("TagType", "Alias"); doc.Save(source); project = await service.RegisterAsync(source);
        await Assert.ThrowsAsync<OperationException>(() => service.CreateBrowserWatchAsync(new(project.Id, project.CurrentSha256, "alias", [new("x", "Record", "WatchProgram")])));
        await service.StartWatchAsync(watch.Definition.Id); await Until(() => service.GetWatch(watch.Definition.Id).Latest is not null);
        Assert.Equal(8, (await service.StopWatchAsync(watch.Definition.Id)).Latest!.Values[0].Value!.GetValue<int>());
    }
    [Fact]
    public async Task RecordsSurviveRestartWithSessionBreaksAndPagingAndNoAutomaticPolling()
    {
        var watch = await Create(); var id = watch.Definition.Id;
        await service.StartWatchAsync(id); await Until(() => service.GetWatch(id).Latest?.Sequence >= 2); await service.StopWatchAsync(id);
        var all = service.QueryRecording(id, new()); Assert.Contains(all.Records, r => r.Kind == "stop");
        var first = service.QueryRecording(id, new(Limit: 1)); Assert.True(first.HasMore);
        Assert.True(service.QueryRecording(id, new(AfterIndex: first.NextIndex)).Records.All(r => r.Index > first.NextIndex));
        service.Dispose(); service = new(options, reader);
        var restored = service.QueryRecording(id, new()); Assert.Contains(restored.Records, r => r.Kind == "service_restart");
        Assert.Equal(all.Records.Count(r => r.Snapshot is not null), service.WatchHistory(id).Length);
        Assert.True(restored.Stale); Assert.Equal("stopped", restored.State); Assert.Equal(1, reader.Opens);
    }
    [Fact]
    public async Task PauseAndResumeRecordingDoesNotStopPollingAndCsvRetainsQualityAndGapEvents()
    {
        var watch = await Create(); var id = watch.Definition.Id;
        service.ConfigureRecording(id, new(new(false), 0)); await service.StartWatchAsync(id);
        await Until(() => service.GetWatch(id).Latest is not null);
        Assert.DoesNotContain(service.QueryRecording(id, new()).Records, r => r.Snapshot is not null);
        service.ConfigureRecording(id, new(new(), 1)); await Until(() => service.QueryRecording(id, new()).Records.Any(r => r.Snapshot is not null));
        reader.Fail = true; await Until(() => service.GetWatch(id).State == "faulted");
        var page = service.QueryRecording(id, new()); Assert.True(page.Stale); Assert.Contains(page.Records, r => r.Kind == "fault");
        var artifact = service.ExportRecording(id, new(Limit: 1));
        var csv = File.ReadAllText(service.Download(artifact.Id).Path);
        Assert.Contains("recording_disabled", csv); Assert.Contains("recording_enabled", csv); Assert.Contains("fault", csv); Assert.Contains("Inner.Count", csv); Assert.Contains("\"good\"", csv);
        await service.SaveArtifactAsync(artifact.Id, Path.Combine(root, "exports", "recording.csv"));
        Assert.Throws<OperationException>(() => service.ConfigureRecording(id, new(new(), 0)));
        Assert.Throws<OperationException>(() => service.ConfigureRecording(id, new(new(MaxMiB: 1), 2)));
    }
    [Fact]
    public async Task RecoversOnlyAnIncompleteFinalAppendAndPreservesOldRevisions()
    {
        var watch = await Create(); var id = watch.Definition.Id;
        await service.StartWatchAsync(id); await Until(() => service.GetWatch(id).Latest is not null); await service.StopWatchAsync(id);
        service.Dispose(); var dir = Path.Combine(options.StateDirectory, "watches", id, "recording");
        File.AppendAllText(Directory.GetFiles(dir, "segment-*.jsonl").Order().Last(), "{\"incomplete\":");
        service = new(options, reader); Assert.NotNull(service.RecordingStatus(id).RecoveryNote);
        await service.SaveWatchAsync(watch.Definition.Settings with { Name = "Revision 1" }, id, 0);
        Assert.Empty(service.QueryRecording(id, new()).Records);
        Assert.Contains(service.QueryRecording(id, new(WatchRevision: 0)).Records, r => r.Snapshot is not null);
        Assert.Contains("Inner.Count", File.ReadAllText(service.Download(service.ExportRecording(id, new(WatchRevision: 0)).Id).Path));
    }
    [Fact]
    public async Task RetentionExpiresSegmentsAndCursorNeverGoesBackwards()
    {
        var watch = await Create(); var id = watch.Definition.Id; service.ConfigureRecording(id, new(new(), 0));
        var index = service.RecordingStatus(id).LastIndex;
        var dir = Path.Combine(options.StateDirectory, "watches", id, "recording");
        foreach (var path in Directory.GetFiles(dir, "segment-*.jsonl")) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        Assert.Empty(service.QueryRecording(id, new()).Records); service.Dispose(); service = new(options, reader);
        Assert.True(service.RecordingStatus(id).LastIndex > index);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
    private sealed class Reader : IWatchSessionFactory, IWatchSession
    {
        public int Opens; public volatile bool Fail;
        public Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct)
        { Opens++; File.Copy(request.SourcePath, Path.Combine(request.WorkDirectory, "metadata.l5x")); return Task.FromResult<IWatchSession>(this); }
        public Task<WatchBridgeReply> ReadAsync(WatchTag[] tags, CancellationToken ct) => Fail ? throw new IOException("Communication gap") :
            Task.FromResult(new WatchBridgeReply(true, [new("Record", "WatchProgram", "WatchRecord", [], JsonNode.Parse("{\"Inner\":{\"Count\":8}}"), "good")]));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
