using System.Text.Json.Nodes;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class WatchRebindTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WatchRebindTests", Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly ServiceOptions options;
    private readonly Reader reader = new();
    private ProjectService service;
    public WatchRebindTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "baseline.l5x"); File.Copy(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"), source);
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root],
            ControllerTargets = [new("configured", "inert-route", new(["GoOnlineAsync", "GetTagValueAsync"], "WatchProgram"), new("192.0.2.1", "1,0"))] };
        service = new(options, reader);
    }
    private WatchListRequest Settings() => new("Rebind demo", [new("record", "Record", "WatchProgram"), new("count", "Record", "WatchProgram", "Inner.Count")],
        ProjectService.Hash(File.ReadAllBytes(source)), SourcePath: source, Mode: "Offline", IntervalMs: 100, HistoryLimit: 7);
    private async Task<ProjectInfo> Destination(Action<XDocument>? edit = null)
    {
        var doc = XmlProjects.Parse(File.ReadAllBytes(source)); edit?.Invoke(doc); var path = Path.Combine(root, "destination-" + Guid.NewGuid().ToString("N") + ".l5x");
        doc.Save(path); return await service.RegisterAsync(path);
    }
    private static WatchRebindPreviewRequest Request(WatchStatus watch, ProjectInfo project, WatchTemplateProgramMap[]? maps = null)
        => new(watch.Definition.Revision, project.Id, project.CurrentSha256, maps);
    private Dictionary<string, string> Files() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("service.lock", StringComparison.Ordinal))
        .ToDictionary(p => p, p => ProjectService.Hash(File.ReadAllBytes(p)), StringComparer.Ordinal);

    [Fact]
    public async Task PreviewIsReadOnlyAndIgnoresStoredValuesDescriptionsAndTypeDeclarationOrder()
    {
        var watch = await service.SaveWatchAsync(Settings());
        var project = await Destination(doc =>
        {
            doc.Descendants("Tag").First().Add(new XElement("Description", "Changed descriptive text"));
            doc.Descendants("DataValueMember").First().SetAttributeValue("Value", "999");
            var container = doc.Descendants("DataTypes").Single(); var types = container.Elements().Reverse().ToArray(); container.RemoveNodes(); container.Add(types);
        });
        var before = Files(); var preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, Request(watch, project));
        Assert.True(preview.CanApply); Assert.Equal("matching_l5x", preview.BaselineStatus); Assert.Equal(watch.Definition.Settings.ExpectedSha256, preview.FromObservedSha256);
        Assert.All(preview.Changes, c => { Assert.Equal(["unchanged"], c.Changes); Assert.True(c.BeforeDeclaration!.Complete); Assert.Equal(c.BeforeDeclaration.Sha256, c.AfterDeclaration!.Sha256); });
        Assert.Equal(before, Files()); Assert.Equal(0, reader.Opens);
    }
    [Fact]
    public async Task NestedTypeChangesAndArrayDimensionsAreVisibleBeforeApply()
    {
        var watch = await service.SaveWatchAsync(Settings() with { Tags = [new("count", "Record", "WatchProgram", "Inner.Count"), new("records", "Records", "WatchProgram")] });
        var project = await Destination(doc =>
        {
            doc.Descendants("DataType").Single(t => (string?)t.Attribute("Name") == "WatchChild").Descendants("Member").Single().SetAttributeValue("DataType", "LINT");
            doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Records").SetAttributeValue("Dimensions", "3");
        });
        var preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, Request(watch, project)); Assert.True(preview.CanApply);
        Assert.Equal("DINT", preview.Changes[0].BeforeDeclaration!.DataType); Assert.Equal("LINT", preview.Changes[0].AfterDeclaration!.DataType);
        Assert.Contains("selected_type_changed", preview.Changes[0].Changes); Assert.Contains("base_declaration_changed", preview.Changes[0].Changes);
        Assert.Contains("selected_dimensions_changed", preview.Changes[1].Changes); Assert.Equal([3], preview.Changes[1].AfterDeclaration!.Dimensions);
    }
    [Fact]
    public async Task ApplyKeepsIdSettingsOldDefinitionsRecordingsAndRetentionButClearsLatestAndPolling()
    {
        var watch = await service.SaveWatchAsync(Settings());
        var recording = service.ConfigureRecording(watch.Definition.Id, new(new(true, 5, 8), 0));
        await service.StartWatchAsync(watch.Definition.Id); await Until(() => service.GetWatch(watch.Definition.Id).Latest is not null); await service.StopWatchAsync(watch.Definition.Id);
        var old = service.QueryRecording(watch.Definition.Id, new(WatchRevision: 0)); Assert.Contains(old.Records, r => r.Snapshot is not null);
        var project = await Destination(doc => doc.Descendants("Program").Single().SetAttributeValue("Name", "DestinationProgram"));
        var request = Request(watch, project, [new("WatchProgram", "DestinationProgram")]); var preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, request);
        Assert.All(preview.Changes, c => Assert.Equal(["scope_changed"], c.Changes));
        var result = await service.ApplyWatchRebindAsync(watch.Definition.Id, new(request, preview.ReviewSha256));
        Assert.Equal(watch.Definition.Id, result.Definition.Id); Assert.Equal(1, result.Definition.Revision); Assert.Equal("stopped", result.State); Assert.Null(result.Latest); Assert.True(result.Stale);
        Assert.Empty(service.WatchHistory(watch.Definition.Id)); Assert.Equal(7, result.Definition.Settings.HistoryLimit); Assert.Equal(100, result.Definition.Settings.IntervalMs);
        Assert.Equal("Rebind demo", result.Definition.Settings.Name); Assert.Equal("Sdk", result.Definition.Settings.Backend); Assert.Equal("Offline", result.Definition.Settings.Mode);
        Assert.All(result.Definition.Settings.Tags, t => Assert.Equal("DestinationProgram", t.ProgramName)); Assert.Null(result.Definition.Settings.SourcePath); Assert.NotNull(result.Definition.Settings.SourceArtifactId);
        Assert.Equal(project.CurrentSha256, result.Definition.Settings.ExpectedSha256); Assert.Equal(recording.Settings, service.RecordingStatus(watch.Definition.Id).Settings);
        var oldAfter = service.QueryRecording(watch.Definition.Id, new(WatchRevision: 0)); Assert.Equal(JsonSerializer.Serialize(old.Records), JsonSerializer.Serialize(oldAfter.Records)); Assert.Equal(watch.Definition.Settings.ExpectedSha256, oldAfter.Definition!.Settings.ExpectedSha256);
        Assert.Contains(service.QueryRecording(watch.Definition.Id, new()).Records, r => r.Kind == "source_rebound" && r.WatchRevision == 1);
        Assert.Equal(1, reader.Opens);
        service.Dispose(); service = new(options, reader); var restored = service.GetWatch(watch.Definition.Id); Assert.Equal(1, restored.Definition.Revision); Assert.Equal("stopped", restored.State); Assert.Null(restored.Latest);
        Assert.Contains(service.QueryRecording(watch.Definition.Id, new(WatchRevision: 0)).Records, r => r.Snapshot is not null);
    }
    [Fact]
    public async Task ChangedOrMissingOldSourcesAreExplicitAndNeverBecomeTheApprovedBaseline()
    {
        var watch = await service.SaveWatchAsync(Settings()); var project = await Destination(); File.AppendAllText(source, " ");
        var request = Request(watch, project); var preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, request);
        Assert.Equal("source_changed", preview.BaselineStatus); Assert.True(preview.CanApply); Assert.All(preview.Changes, c => { Assert.Null(c.BeforeDeclaration); Assert.Contains("before_unavailable", c.Changes); });
        File.Delete(source);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyWatchRebindAsync(watch.Definition.Id, new(request, preview.ReviewSha256)))).Code);
        preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, request); Assert.Equal("source_unavailable", preview.BaselineStatus); Assert.Null(preview.FromObservedSha256);
        Assert.True(preview.CanApply); var result = await service.ApplyWatchRebindAsync(watch.Definition.Id, new(request, preview.ReviewSha256)); Assert.Equal(1, result.Definition.Revision); Assert.Equal(0, reader.Opens);
    }
    [Fact]
    public async Task StaleWatchDestinationMappingOrReviewCannotMutateTheListOrCreateArtifacts()
    {
        var watch = await service.SaveWatchAsync(Settings()); var project = await Destination(); var request = Request(watch, project);
        var preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, request); var before = Files();
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyWatchRebindAsync(watch.Definition.Id, new(request, new string('0', 64))))).Code);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyWatchRebindAsync(watch.Definition.Id, new(request with { ProgramMappings = [new("WatchProgram", null)] }, preview.ReviewSha256)));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyWatchRebindAsync(watch.Definition.Id, new(request with { ExpectedRevision = 1 }, preview.ReviewSha256)));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyWatchRebindAsync(watch.Definition.Id, new(request with { ExpectedSha256 = "stale" }, preview.ReviewSha256)));
        Assert.Equal(before, Files()); Assert.Equal(0, service.GetWatch(watch.Definition.Id).Definition.Revision); Assert.Equal(0, reader.Opens);
        await service.SaveWatchAsync(Settings() with { Name = "Changed definition" }, watch.Definition.Id, 0);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyWatchRebindAsync(watch.Definition.Id, new(request, preview.ReviewSha256)));
    }
    [Fact]
    public async Task RunningWatchRejectsPreviewAndLateApplyWithoutAnySourceArtifact()
    {
        var watch = await service.SaveWatchAsync(Settings()); var project = await Destination(); var request = Request(watch, project); var preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, request);
        await service.StartWatchAsync(watch.Definition.Id); await Until(() => service.GetWatch(watch.Definition.Id).Latest is not null);
        var artifacts = Directory.GetDirectories(Path.Combine(options.StateDirectory, "artifacts"));
        Assert.Equal("watch_running", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewWatchRebindAsync(watch.Definition.Id, request))).Code);
        Assert.Equal("watch_running", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyWatchRebindAsync(watch.Definition.Id, new(request, preview.ReviewSha256)))).Code);
        Assert.Equal(artifacts, Directory.GetDirectories(Path.Combine(options.StateDirectory, "artifacts"))); await service.StopWatchAsync(watch.Definition.Id);
    }
    [Fact]
    public async Task DestinationPolicyAndMissingMembersRemainBlockingEvenWhenPollingIsDisabled()
    {
        var watch = await service.SaveWatchAsync(Settings() with { Mode = "Online", ControllerTargetId = "configured" });
        var project = await Destination(doc => doc.Descendants("Program").Single().SetAttributeValue("Name", "OtherProgram"));
        var request = Request(watch, project, [new("WatchProgram", "OtherProgram")]); var preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, request);
        Assert.False(preview.CanApply); Assert.Contains(preview.Preflight.Selections, s => s.Issues.Any(i => i.Code == "sdk_target_policy"));
        Assert.Equal("watch_rebind_binding", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyWatchRebindAsync(watch.Definition.Id, new(request, preview.ReviewSha256)))).Code);
        Assert.Equal(0, reader.Opens); Assert.False(options.EnableControllerOperations);
    }
    [Fact]
    public async Task MissingBuiltInTemplatesArePartialAndCutoverIsRecordedWhenRecordingIsPaused()
    {
        var watch = await service.SaveWatchAsync(Settings() with { Tags = [new("text", "Message", "WatchProgram")] }); var project = await Destination();
        service.ConfigureRecording(watch.Definition.Id, new(new(false, 7, 8), 0));
        var request = Request(watch, project); var preview = await service.PreviewWatchRebindAsync(watch.Definition.Id, request);
        Assert.True(preview.CanApply); Assert.Contains("declarations_partial", preview.Changes.Single().Changes); Assert.False(preview.Changes[0].BeforeDeclaration!.Complete);
        await service.ApplyWatchRebindAsync(watch.Definition.Id, new(request, preview.ReviewSha256));
        Assert.False(service.RecordingStatus(watch.Definition.Id).Settings.Enabled); Assert.Contains(service.QueryRecording(watch.Definition.Id, new()).Records, r => r.Kind == "source_rebound");
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private sealed class Reader : IWatchSessionFactory, IWatchSession
    {
        public int Opens;
        public Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct)
        { Opens++; File.Copy(request.SourcePath, Path.Combine(request.WorkDirectory, "metadata.l5x")); return Task.FromResult<IWatchSession>(this); }
        public Task<WatchBridgeReply> ReadAsync(WatchTag[] tags, CancellationToken ct) => Task.FromResult(new WatchBridgeReply(true,
            [new("Record", "WatchProgram", "WatchRecord", [], JsonNode.Parse("{\"Inner\":{\"Count\":8}}"), "good")]));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
