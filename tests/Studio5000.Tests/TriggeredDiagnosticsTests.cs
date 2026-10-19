using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class TriggeredDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TriggeredDiagnostics", Guid.NewGuid().ToString("N"));
    private readonly Reader reader = new();
    private readonly ServiceOptions options;
    private ProjectService service;
    public TriggeredDiagnosticsTests()
    {
        Directory.CreateDirectory(root); File.Copy(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"), Path.Combine(root, "demo.l5x"));
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] }; service = new(options, reader);
    }
    private sealed class Reader : IWatchSessionFactory
    {
        internal int Count, FailAfter = int.MaxValue;
        internal Func<int, int> Value = i => i / 3 % 2;
        public async Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct)
        {
            await File.WriteAllBytesAsync(Path.Combine(request.WorkDirectory, "metadata.l5x"), await File.ReadAllBytesAsync(request.SourcePath, ct), ct);
            return new Session(this);
        }
        private sealed class Session(Reader owner) : IWatchSession
        {
            public Task<WatchBridgeReply> ReadAsync(WatchTag[] tags, CancellationToken ct)
            {
                var n = Interlocked.Increment(ref owner.Count);
                return Task.FromResult(n > owner.FailAfter ? new WatchBridgeReply(false, [], "Generated communication fault") : new WatchBridgeReply(true,
                    [new("Record", "WatchProgram", "WatchRecord", [], new JsonObject { ["Inner"] = new JsonObject { ["Count"] = owner.Value(n) } }, "good")]));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private async Task<(ProjectInfo Project, WatchStatus Watch)> Watch()
    {
        var p = await service.RegisterAsync(Path.Combine(root, "demo.l5x"));
        var w = await service.SaveWatchAsync(new("Generated values", [new("count", "Record", "WatchProgram", "Inner.Count")], p.CurrentSha256, SourcePath: Path.Combine(root, "demo.l5x"), Mode: "Offline", IntervalMs: 100));
        return (p, w);
    }
    private Task<DiagnosticTriggerStatus> Trigger(ProjectInfo p, WatchStatus w, string kind = "change", int post = 2) => service.SaveDiagnosticTriggerAsync(new("Generated diagnostic", p.Id, p.CurrentSha256,
        w.Definition.Id, w.Definition.Revision, new(kind, "count", Operator: "eq", Value: JsonSerializer.SerializeToElement(1), HoldMs: 200), 2, post));
    private static async Task Until(Func<bool> predicate)
    { var end = DateTime.UtcNow.AddSeconds(20); while (!predicate()) { if (DateTime.UtcNow > end) throw new TimeoutException("Generated watch condition timed out."); await Task.Delay(15); } }
    [Fact]
    public async Task TriggerRetainsPreAndPostSamplesWithoutStartingPollingAndSurvivesRestartDisarmed()
    {
        var (p, w) = await Watch(); var trigger = await Trigger(p, w);
        await service.ArmDiagnosticTriggerAsync(trigger.Definition.Id, new(0)); Assert.Equal(0, reader.Count);
        await service.StartWatchAsync(w.Definition.Id); await Until(() => service.DiagnosticTriggers().Single().State == "complete"); await service.StopWatchAsync(w.Definition.Id);
        var status = service.DiagnosticTriggers().Single(); var summary = Assert.Single(status.Episodes); var episode = service.GetDiagnosticEpisode(status.Definition.Id, summary.Id);
        Assert.Equal("completed", episode.Status); Assert.Equal(5, episode.Frames.Length); Assert.True(episode.Frames[2].Trigger); Assert.Equal(0, episode.DroppedPreSamples);
        Assert.Equal(p.CurrentSha256, episode.Definition.ProjectSha256); Assert.Equal(w.Definition.Revision, episode.Definition.Watch.Revision);
        var capture = await service.CaptureDiagnosticEpisodeAsync(trigger.Definition.Id, episode.Id, new(episode.Artifact!.Sha256)); Assert.Equal(p.CurrentSha256, capture.ProjectSha256);
        service.Dispose(); service = new(options, reader); Assert.Equal("disarmed", service.DiagnosticTriggers().Single().State); Assert.Single(service.DiagnosticTriggers().Single().Episodes);
    }
    [Fact]
    public async Task FaultTriggersRetainInterruptedWindowsAndDoNotInventPostSamples()
    {
        reader.FailAfter = 3; var (p, w) = await Watch(); var trigger = await Trigger(p, w, "fault"); await service.ArmDiagnosticTriggerAsync(trigger.Definition.Id, new(0));
        await service.StartWatchAsync(w.Definition.Id); await Until(() => service.GetWatch(w.Definition.Id).State == "faulted");
        var summary = Assert.Single(service.DiagnosticTriggers().Single().Episodes); var e = service.GetDiagnosticEpisode(trigger.Definition.Id, summary.Id);
        Assert.Equal("interrupted", e.Status); Assert.Equal("fault", Assert.Single(e.Frames, f => f.Trigger).Kind); Assert.NotEmpty(e.Diagnostics);
        await Assert.ThrowsAsync<OperationException>(() => service.CaptureDiagnosticEpisodeAsync(trigger.Definition.Id, e.Id, new(e.Artifact!.Sha256)));
        Assert.Equal("trigger_episode", (await service.CaptureDiagnosticEpisodeAsync(trigger.Definition.Id, e.Id, new(e.Artifact!.Sha256, 0))).Origin);
    }
    [Theory]
    [InlineData("predicate")]
    [InlineData("timeout")]
    public async Task ScalarAndTimeoutConditionsUseObservedSamples(string kind)
    {
        reader.Value = _ => 1; var (p, w) = await Watch(); var trigger = await Trigger(p, w, kind, 0); await service.ArmDiagnosticTriggerAsync(trigger.Definition.Id, new(0));
        await service.StartWatchAsync(w.Definition.Id); await Until(() => service.DiagnosticTriggers().Single().State == "complete"); await service.StopWatchAsync(w.Definition.Id);
        var episode = service.GetDiagnosticEpisode(trigger.Definition.Id, service.DiagnosticTriggers().Single().Episodes.Single().Id);
        if (kind == "timeout") Assert.True(episode.Frames.Single(f => f.Trigger).Sample!.Sequence >= 3);
        Assert.Equal("completed", episode.Status);
    }
    [Fact]
    public async Task StoppingDuringPostWindowRecordsAnExplicitInterruptionAndDefinitionEditsUseRevision()
    {
        var (p, w) = await Watch(); var trigger = await Trigger(p, w, post: 20); await service.ArmDiagnosticTriggerAsync(trigger.Definition.Id, new(0));
        await service.StartWatchAsync(w.Definition.Id); await Until(() => service.DiagnosticTriggers().Single().State == "capturing"); await service.StopWatchAsync(w.Definition.Id);
        Assert.Equal("interrupted", service.DiagnosticTriggers().Single().Episodes.Single().Status);
        await Assert.ThrowsAsync<OperationException>(() => service.ArmDiagnosticTriggerAsync(trigger.Definition.Id, new(9)));
        service.DeleteDiagnosticTrigger(trigger.Definition.Id, new(0)); Assert.Empty(service.DiagnosticTriggers());
    }
    [Fact]
    public async Task SavedTrendViewsModifyIndependentlyAndGatherBoundedVerifiedResults()
    {
        reader.Value = i => i; var (_, w) = await Watch(); await service.StartWatchAsync(w.Definition.Id); await Until(() => service.GetWatch(w.Definition.Id).Latest?.Sequence >= 6); await service.StopWatchAsync(w.Definition.Id);
        var view = await service.SaveTrendAsync(new("Count trend", w.Definition.Id, 0, ["count"]));
        var updated = await service.SaveTrendAsync(new("Count trend updated", w.Definition.Id, 0, ["count"], 20, view.Id, 0)); Assert.Equal(1, updated.Revision);
        await Assert.ThrowsAsync<OperationException>(() => service.SaveTrendAsync(new("Stale", w.Definition.Id, 0, ["count"], Id: view.Id, ExpectedRevision: 0)));
        var result = service.AnalyzeTrend(new(view.Id, 1)); Assert.Single(result.Statistics); Assert.True(result.Statistics[0].GoodSamples >= 6); Assert.Equal("1", result.Statistics[0].FirstExact);
        Assert.Equal(0, result.Statistics[0].MissingSamples); // Start/stop/gap markers are not sampled values.
        var partial = service.AnalyzeTrend(new(view.Id, 1, result.From, result.To, Limit: 2)); Assert.True(partial.HasMore); Assert.True(partial.NextIndex > 0);
        var artifact = service.ExportTrendAnalysis(new(new(view.Id, 1, result.From, result.To), result.ResultSha256)); Assert.Equal("json", artifact.Format);
        Assert.Throws<OperationException>(() => service.ExportTrendAnalysis(new(new(view.Id, 1, result.From, result.To), "wrong")));
        service.Dispose(); service = new(options, reader); Assert.Equal(view.Id, Assert.Single(service.SavedTrends()).Id); Assert.Equal(0, service.GetWatch(w.Definition.Id).Definition.Revision);
    }
    [Fact]
    public async Task SavedTrendSelectsNestedMembersBeforeTheGlobalPlotSeriesLimit()
    {
        var path = Path.Combine(root, "demo.l5x"); var hash = ProjectService.Hash(await File.ReadAllBytesAsync(path));
        var w = await service.SaveWatchAsync(new("Structure trend", [new("record", "Record", "WatchProgram")], hash, SourcePath: path, Mode: "Offline", IntervalMs: 100));
        await service.StartWatchAsync(w.Definition.Id); await Until(() => service.GetWatch(w.Definition.Id).Latest?.Sequence >= 2); await service.StopWatchAsync(w.Definition.Id);
        foreach (var selected in new[] { "record", "record.Inner.Count" })
        {
            var view = await service.SaveTrendAsync(new("Nested selection", w.Definition.Id, 0, [selected]));
            Assert.Equal("record.Inner.Count", Assert.Single(service.AnalyzeTrend(new(view.Id, 0)).Statistics).Key);
        }
        var at = DateTimeOffset.UtcNow;
        var large = new JsonObject(); for (var i = 0; i < 150; i++) large["m" + i] = i;
        var snapshot = new WatchSnapshot(1, at, at, "Offline", [new("record", "Record", "WatchProgram", "WatchRecord", large, "good")]);
        var trend = WatchTrends.Build([new(1, 0, "generated", at, "sample", snapshot)], 100, at.AddSeconds(-1), at.AddSeconds(1), true, key => key == "record.m149");
        Assert.Equal("record.m149", Assert.Single(trend.Series).Key); Assert.False(trend.SeriesLimitReached);
    }
    [Fact]
    public async Task BadTriggerSettingsOrStaleBindingDoNotStartAReader()
    {
        var (p, w) = await Watch();
        await Assert.ThrowsAsync<OperationException>(() => service.SaveDiagnosticTriggerAsync(new("Bad", p.Id, "wrong", w.Definition.Id, 0, new("change", "count"))));
        await Assert.ThrowsAsync<OperationException>(() => service.SaveDiagnosticTriggerAsync(new("Bad", p.Id, p.CurrentSha256, w.Definition.Id, 0, new("predicate", "missing", Value: JsonSerializer.SerializeToElement(1)))));
        Assert.Empty(service.DiagnosticTriggers()); Assert.Equal(0, reader.Count);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
