using System.Text.Json.Nodes;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class WatchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WatchTests", Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly FakeReader reader = new();
    private ProjectService service;
    private readonly ServiceOptions options;
    public WatchTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "fixture.l5x"); File.Copy(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"), source);
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], ControllerTargets = [new("lab", "configured-path", new(["GoOnlineAsync", "GetTagValueAsync"], "WatchProgram", "main"))] };
        service = new(options, reader);
    }
    private WatchListRequest Request() => new("Process values", [new("record", "Record", "WatchProgram"), new("count", "Record", "WatchProgram", "Inner.Count")],
        ProjectService.Hash(File.ReadAllBytes(source)), SourcePath: source, Mode: "Offline", IntervalMs: 100, HistoryLimit: 2);

    [Fact]
    public async Task ExtractsNestedTypesValuesAndArrayWithoutMemoryOffsets()
    {
        var project = await service.RegisterAsync(source);
        var result = service.ExtractStructures(new(ProjectId: project.Id, TagName: "Record", ProgramName: "WatchProgram"));
        Assert.Equal("saved_project", result.Origin); Assert.Null(result.UploadedAt);
        Assert.Equal(2, result.Types.Length);
        Assert.Equal([3], result.Types.Single(t => t.Name == "WatchRecord").Members.Single(m => m.Name == "Samples").Dimensions);
        var tag = Assert.Single(result.Tags);
        Assert.Equal(8L, tag.Value!["Inner"]!["Count"]!.GetValue<long>());
        Assert.Equal(20L, tag.Value["Samples"]![1]!.GetValue<long>());
        Assert.Equal(1.5, tag.Value["Position"]!.GetValue<double>());
        Assert.Equal("snapshot", tag.Quality);
    }

    [Fact]
    public async Task PollingIsSerialHistoryIsBoundedAndStopMakesSamplesStale()
    {
        reader.Delay = 140;
        var hash = Request().ExpectedSha256;
        var watch = await service.SaveWatchAsync(Request());
        Assert.Equal("stopped", watch.State); Assert.Equal(0, reader.Opens);
        await service.StartWatchAsync(watch.Definition.Id);
        await Until(() => service.GetWatch(watch.Definition.Id).Latest?.Sequence >= 3);
        var stopped = await service.StopWatchAsync(watch.Definition.Id);
        Assert.Equal("stopped", stopped.State); Assert.True(stopped.Stale);
        Assert.Equal(1, reader.MaxConcurrent); Assert.True(reader.Closed);
        Assert.Equal(2, service.WatchHistory(watch.Definition.Id).Length);
        Assert.Equal(8, stopped.Latest!.Values[1].Value!.GetValue<int>());
        Assert.All(stopped.Latest.Values, v => Assert.Equal("good", v.Quality));
        Assert.Equal(hash, ProjectService.Hash(File.ReadAllBytes(source)));
    }

    [Fact]
    public async Task RestartRetainsDefinitionAndLastSampleButNeverRestartsPolling()
    {
        var watch = await service.SaveWatchAsync(Request()); await service.StartWatchAsync(watch.Definition.Id);
        await Until(() => service.GetWatch(watch.Definition.Id).Latest is not null);
        service.Dispose(); var restartedReader = new FakeReader(); service = new(options, restartedReader);
        var restored = Assert.Single(service.WatchLists());
        Assert.Equal("stopped", restored.State); Assert.True(restored.Stale); Assert.NotNull(restored.Latest);
        Assert.Equal(0, restartedReader.Opens);
    }

    [Fact]
    public async Task StaleSourceFailsBeforeOpeningReaderAndDoesNotRetry()
    {
        var watch = await service.SaveWatchAsync(Request()); File.AppendAllText(source, " ");
        await service.StartWatchAsync(watch.Definition.Id); await Until(() => service.GetWatch(watch.Definition.Id).State == "faulted");
        Assert.Equal(0, reader.Opens); Assert.True(service.GetWatch(watch.Definition.Id).Stale);
    }

    [Fact]
    public async Task OnlineStartIsGatedAndScopedEvenThoughSavingNeverConnects()
    {
        var request = Request() with { Mode = "Online", ControllerTargetId = "lab" };
        var watch = await service.SaveWatchAsync(request);
        var ex = await Assert.ThrowsAsync<OperationException>(() => service.StartWatchAsync(watch.Definition.Id)); Assert.Equal("sdk_controller_disabled", ex.Code);
        foreach (var tag in new[] { new WatchTag("x", "Record"), new WatchTag("x", "Record", "Other") })
            Assert.Equal("sdk_target_policy", (await Assert.ThrowsAsync<OperationException>(() => service.SaveWatchAsync(request with { Tags = [tag] }))).Code);
        Assert.Equal(0, reader.Opens);
    }

    [Fact]
    public async Task MissingMemberIsBadWithoutHidingSuccessfulValues()
    {
        var watch = await service.SaveWatchAsync(Request() with { Tags = [new("good", "Record", "WatchProgram"), new("bad", "Record", "WatchProgram", "Missing[3]")] });
        await service.StartWatchAsync(watch.Definition.Id); await Until(() => service.GetWatch(watch.Definition.Id).Latest is not null);
        var stopped = await service.StopWatchAsync(watch.Definition.Id);
        Assert.Equal("good", stopped.Latest!.Values[0].Quality);
        Assert.Equal("bad", stopped.Latest.Values[1].Quality); Assert.Null(stopped.Latest.Values[1].Value);
    }

    [Fact]
    public async Task ReaderFailureStopsWithStaleDataAndNoAutomaticReconnect()
    {
        reader.FailAfter = 1; var watch = await service.SaveWatchAsync(Request());
        await service.StartWatchAsync(watch.Definition.Id); await Until(() => service.GetWatch(watch.Definition.Id).State == "faulted");
        Assert.True(service.GetWatch(watch.Definition.Id).Stale); Assert.NotNull(service.GetWatch(watch.Definition.Id).Latest);
        Assert.Equal(1, reader.Opens); Assert.True(reader.Closed);
    }

    [Fact]
    public async Task EditsRequireStoppedListAndCurrentRevision()
    {
        var watch = await service.SaveWatchAsync(Request()); await service.StartWatchAsync(watch.Definition.Id);
        Assert.Equal("watch_running", (await Assert.ThrowsAsync<OperationException>(() => service.SaveWatchAsync(Request(), watch.Definition.Id, 0))).Code);
        await service.StopWatchAsync(watch.Definition.Id);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.SaveWatchAsync(Request(), watch.Definition.Id, 99))).Code);
        var changed = await service.SaveWatchAsync(Request() with { IntervalMs = 500 }, watch.Definition.Id, 0);
        Assert.Equal(1, changed.Definition.Revision); Assert.Null(changed.Latest);
        service.DeleteWatch(watch.Definition.Id); Assert.Empty(service.WatchLists());
    }

    [Theory]
    [InlineData("Tag']/*", null)] [InlineData("Record", "Inner..Count")] [InlineData("Record", "Samples[-1]")]
    public async Task RejectsSelectorsAndInvalidMembers(string tag, string? member)
    { await Assert.ThrowsAsync<OperationException>(() => service.SaveWatchAsync(Request() with { Tags = [new("x", tag, "WatchProgram", member)] })); }

    [Fact]
    public void DecodesMultiDimensionalArraysSignedRadixAndRejectsMissingCells()
    {
        var tag = System.Xml.Linq.XElement.Parse("<Tag Name='Values' DataType='INT' Dimensions='2,2'><Data Format='Decorated'><Array DataType='INT' Dimensions='2,2'><Element Index='[0,0]' Value='16#FFFF'/><Element Index='[0,1]' Value='2'/><Element Index='[1,0]' Value='3'/><Element Index='[1,1]' Value='4'/></Array></Data></Tag>");
        var value = LogixStructures.ExtractTag(tag, null);
        Assert.Equal((short)-1, value.Value![0]![0]!.GetValue<short>()); Assert.Equal(4L, value.Value[1]![1]!.GetValue<long>());
        tag.Descendants("Element").Last().Remove(); Assert.Equal("bad", LogixStructures.ExtractTag(tag, null).Quality);
    }

    private static async Task Until(Func<bool> test)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!test()) await Task.Delay(20, timeout.Token); }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }

    [Fact]
    public void ExtractsEscapedStringsAndSelectsRootArrayMembers()
    {
        var tag = System.Xml.Linq.XElement.Parse("<Tag Name='Text' DataType='STRING'><Data Format='String'><![CDATA['O$'Brien$$$0A']]></Data></Tag>");
        Assert.Equal("O'Brien$\n", LogixStructures.ExtractTag(tag, null).Value!.GetValue<string>());
        var array = JsonNode.Parse("[{\"Count\":11},{\"Count\":22}]");
        Assert.Equal(22, LogixStructures.Select(array, "[1].Count")!.GetValue<int>());
        Assert.Throws<OperationException>(() => LogixStructures.Select(array, "[9].Count"));
    }

    [Fact]
    public async Task ReturnedDefinitionsCannotMutateTheSharedWatchConfiguration()
    {
        var saved = await service.SaveWatchAsync(Request()); saved.Definition.Settings.Tags[0] = new("x", "Foreign");
        Assert.Equal("Record", service.GetWatch(saved.Definition.Id).Definition.Settings.Tags[0].TagName);
    }

    [Fact]
    public async Task UploadProvenanceRequiresAnUnmodifiedSuccessfulUploadResult()
    {
        var project = await service.RegisterAsync(source); var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(options.StateDirectory, "sdk-jobs", id); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "job.json");
        var job = new SdkJob(id, "succeeded", DateTimeOffset.UtcNow, ["UploadToNewProjectAsync"], null, "lab", true, [], [], [], project);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(job, ProjectService.Json));
        var uploaded = service.ExtractStructures(new(JobId: id, TagName: "Record"));
        Assert.Equal("controller_upload_snapshot", uploaded.Origin); Assert.Equal(project.CreatedAt, uploaded.UploadedAt);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(job with { Methods = ["UploadToNewProjectAsync", "SetTagValueDINTAsync"] }, ProjectService.Json));
        Assert.Equal("saved_project", service.ExtractStructures(new(JobId: id, TagName: "Record")).Origin);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(job with { Status = "indeterminate" }, ProjectService.Json));
        Assert.Equal("structure_job", Assert.Throws<OperationException>(() => service.ExtractStructures(new(JobId: id))).Code);
    }

    [Fact]
    public async Task LiveStructurePullCannotBypassDisabledControllerGate()
    {
        options.NativeBridgePath = source; // Inert file satisfies availability; it must never be executed.
        Assert.Equal("sdk_controller_disabled", (await Assert.ThrowsAsync<OperationException>(() => service.PullStructuresAsync(new("lab")))).Code);
        Assert.Empty(service.SdkJobList()); Assert.Equal(0, reader.Opens);
    }

    [Fact]
    public void NestedStringMembersAndAsciiByteArraysRetainTheirValues()
    {
        var tag = System.Xml.Linq.XElement.Parse("""
            <Tag Name="Record" DataType="RecordType"><Data Format="Decorated"><Structure DataType="RecordType">
            <StructureMember Name="Label" DataType="STRING"><DataValueMember Name="LEN" DataType="DINT" Value="6"/>
            <DataValueMember Name="DATA" DataType="STRING" Radix="ASCII" Value="'Nested'"/></StructureMember>
            <ArrayMember Name="Bytes" DataType="SINT" Dimensions="2" Radix="ASCII"><Element Index="[0]" Value="'A'"/><Element Index="[1]" Value="'$FF'"/></ArrayMember>
            </Structure></Data></Tag>
            """);
        var result = LogixStructures.ExtractTag(tag, null);
        Assert.Equal("snapshot", result.Quality); Assert.Equal("Nested", result.Value!["Label"]!["DATA"]!.GetValue<string>());
        Assert.Equal((sbyte)65, result.Value["Bytes"]![0]!.GetValue<sbyte>()); Assert.Equal((sbyte)-1, result.Value["Bytes"]![1]!.GetValue<sbyte>());
    }

    [Fact]
    public async Task LargeProjectsReturnAllTypeDefinitionsWithPagedTagValues()
    {
        var doc = XmlProjects.Parse(File.ReadAllBytes(source));
        var tags = doc.Descendants("Program").Single().Element("Tags")!; var template = tags.Elements("Tag").First();
        for (var i = 0; i < 270; i++) { var clone = new System.Xml.Linq.XElement(template); clone.SetAttributeValue("Name", "Extra" + i); tags.Add(clone); }
        doc.Save(source); var project = await service.RegisterAsync(source);
        var page = service.ExtractStructures(new(ProjectId: project.Id, IncludeValues: false, Offset: 256, Limit: 32));
        Assert.Equal(274, page.TotalTags); Assert.Equal(18, page.Tags.Length); Assert.Equal(2, page.Types.Length);
        Assert.All(page.Tags, tag => Assert.Equal("schema_only", tag.Quality));
        Assert.Empty(service.ExtractStructures(new(ProjectId: project.Id, Offset: 1000)).Tags);
        Assert.Throws<OperationException>(() => service.ExtractStructures(new(ProjectId: project.Id, Limit: 257)));
    }

    private sealed class FakeReader : IWatchSessionFactory, IWatchSession
    {
        public int Opens, Delay, MaxConcurrent, FailAfter = int.MaxValue;
        private int active, reads;
        public bool Closed;
        public async Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct)
        { Opens++; await File.WriteAllTextAsync(Path.Combine(request.WorkDirectory, "metadata.l5x"), await File.ReadAllTextAsync(request.SourcePath, ct), ct); return this; }
        public async Task<WatchBridgeReply> ReadAsync(WatchTag[] tags, CancellationToken ct)
        {
            var count = Interlocked.Increment(ref active); MaxConcurrent = Math.Max(MaxConcurrent, count);
            try
            {
                if (++reads > FailAfter) throw new IOException("Disconnected.");
                await Task.Delay(Delay, ct);
                return new(true, [new("Record", "WatchProgram", "WatchRecord", [], JsonNode.Parse("{\"Inner\":{\"Count\":8},\"Samples\":[10,20,30]}"), "good")]);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
    }
}
