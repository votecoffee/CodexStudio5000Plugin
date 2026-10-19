using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class CipWatchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CipWatchTests", Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly FakeReader reader = new();
    private readonly ServiceOptions options;
    private readonly ProjectService service;
    public CipWatchTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "fixture.l5x"); File.Copy(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"), source);
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root],
            ControllerTargets = [new("lab", "configured-linx-path", new(["GoOnlineAsync", "GetTagValueAsync"], "WatchProgram"), new("192.0.2.1", "1,0"))] };
        service = new(options, reader);
    }
    private XDocument Doc() => XmlProjects.Parse(File.ReadAllBytes(source));
    private WatchListRequest Request() => new("CIP demo", [new("count", "Record", "WatchProgram", "Inner.Count")],
        ProjectService.Hash(File.ReadAllBytes(source)), SourcePath: source, ControllerTargetId: "lab", IntervalMs: 100, Backend: "LibPlcTag");
    private static IEnumerable<CipReadNode> Leaves(CipReadNode n) => n.Kind == "scalar" ? [n] : n.Children.SelectMany(Leaves);

    [Fact]
    public async Task ExtractsStructuresBySymbolAndReadsDuplicatesOnlyOnce()
    {
        var plan = CipWatchPlanning.Build(Doc(), "hash", [new("all", "Record", "WatchProgram"), new("duplicate", "Record", "WatchProgram", "Inner.Count")]);
        Assert.Equal(6, plan.UniqueLeafReads);
        Assert.Contains(Leaves(plan.Selections[0].Value), n => n.Address == "Program:WatchProgram.Record.Samples[2]" && n.CipType == 0xc3);
        var readCount = 0;
        var values = await CipWatchPlanning.ReadAsync(plan, (node, _) => { readCount++; return Task.FromResult<JsonNode?>(JsonValue.Create(42)); }, default);
        Assert.Equal(6, readCount); Assert.Equal(42, values[0].Value!["Inner"]!["Count"]!.GetValue<int>());
        Assert.Equal(3, values[0].Value!["Samples"]!.AsArray().Count); Assert.Equal(42, values[1].Value!.GetValue<int>());
        values[0].Value!["Inner"]!["Count"] = 99; Assert.Equal(42, values[1].Value!.GetValue<int>());
    }
    [Fact]
    public void MultidimensionalSelectionsRetainCombinedCipIndices()
    {
        var doc = Doc(); var tag = doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Records"); tag.SetAttributeValue("Dimensions", "2,3");
        var plan = CipWatchPlanning.Build(doc, "hash", [new("slice", "Records", "WatchProgram", "[1]"), new("leaf", "Records", "WatchProgram", "[1][2].Inner.Count")]);
        Assert.Equal(18, plan.UniqueLeafReads); Assert.Equal("Program:WatchProgram.Records[1,2].Inner.Count", plan.Selections[1].Value.Address);
        Assert.Equal([3], plan.Selections[0].Value.Dimensions);
    }
    [Theory]
    [InlineData("BOOL", 0xc1)] [InlineData("BIT", 0xc1)] [InlineData("SINT", 0xc2)] [InlineData("INT", 0xc3)]
    [InlineData("DINT", 0xc4)] [InlineData("LINT", 0xc5)] [InlineData("USINT", 0xc6)] [InlineData("UINT", 0xc7)]
    [InlineData("UDINT", 0xc8)] [InlineData("ULINT", 0xc9)] [InlineData("REAL", 0xca)] [InlineData("LREAL", 0xcb)]
    public void LiveTypeIsCheckedExactlyIncludingUnsignedAndWideTypes(string type, int code)
    {
        var doc = Doc(); doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Enabled").SetAttributeValue("DataType", type);
        var node = CipWatchPlanning.Build(doc, "hash", [new("v", "Enabled", "WatchProgram")]).Selections[0].Value;
        CipWatchPlanning.ValidateType(node, [(byte)code, 0]);
        foreach (var invalid in new byte[][] { [], [(byte)code], [(byte)code, 1], [0xa0, 0x02, 0, 0], [(byte)(code + 1), 0] })
            Assert.Equal("cip_type_mismatch", Assert.Throws<OperationException>(() => CipWatchPlanning.ValidateType(node, invalid)).Code);
    }
    [Theory]
    [InlineData("Message", null, "cip_type_unsupported")] [InlineData("Record", "Missing", "cip_member")]
    [InlineData("Records", "[2]", "cip_member")] [InlineData("Record", "Samples[3]", "cip_member")]
    [InlineData("Records", "Inner.Count", "cip_member")] [InlineData("Record", "Samples[-1]", "watch_member")]
    public void UnsupportedOrInvalidSelectorsFailBeforeAnyRead(string tag, string? member, string code)
        => Assert.Equal(code, Assert.Throws<OperationException>(() => CipWatchPlanning.Build(Doc(), "hash", [new("v", tag, "WatchProgram", member)])).Code);
    [Fact]
    public void RejectsAliasesExternalAccessAndOversizedOrRecursiveDeclarations()
    {
        var doc = Doc(); var tag = doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Record");
        tag.SetAttributeValue("AliasFor", "Other"); Fail("watch_alias"); tag.Attribute("AliasFor")!.Remove();
        tag.SetAttributeValue("ExternalAccess", "None"); Fail("cip_external_access"); tag.SetAttributeValue("ExternalAccess", "Read Only");
        var member = doc.Descendants("DataType").Single(t => (string?)t.Attribute("Name") == "WatchRecord").Descendants("Member").First();
        member.SetAttributeValue("ExternalAccess", "None"); Fail("cip_external_access"); member.Attribute("ExternalAccess")!.Remove();
        tag.SetAttributeValue("Dimensions", "257"); Fail("cip_plan_limit"); tag.Attribute("Dimensions")!.Remove();
        member.SetAttributeValue("DataType", "WatchRecord"); Fail("cip_plan");
        void Fail(string code) => Assert.Equal(code, Assert.Throws<OperationException>(() => CipWatchPlanning.Build(doc, "hash", [new("v", "Record", "WatchProgram")])).Code);
    }
    [Fact]
    public void HiddenBackingMembersAreOmittedButCannotBeSelected()
    {
        var doc = Doc(); var member = doc.Descendants("DataType").Single(t => (string?)t.Attribute("Name") == "WatchRecord").Descendants("Member").First();
        member.SetAttributeValue("Hidden", "true");
        Assert.Equal(5, CipWatchPlanning.Build(doc, "hash", [new("v", "Record", "WatchProgram")]).UniqueLeafReads);
        Assert.Throws<OperationException>(() => CipWatchPlanning.Build(doc, "hash", [new("v", "Record", "WatchProgram", (string)member.Attribute("Name")!)]));
    }
    [Theory]
    [InlineData("192.0.2.1&name=Other", "1,0")] [InlineData("localhost", "1,0")] [InlineData("224.0.0.1", "1,0")]
    [InlineData("0.1.2.3", "1,0")] [InlineData("192.0.2.1", "1,0&auto_sync_write_ms=1")]
    [InlineData("192.0.2.1", "1")] [InlineData("192.0.2.1", "1,256")] [InlineData("192.0.2.1", "1,0,1")]
    public void TargetAttributesCannotInjectDriverOptions(string gateway, string route)
        => Assert.Throws<OperationException>(() => CipWatchPlanning.ValidateTarget(new(gateway, route)));
    [Fact]
    public async Task SavingAndPlanningNeverOpenReaderAndStartHonorsControllerAndProgramGates()
    {
        var project = await service.RegisterAsync(source);
        Assert.Equal(1, service.PlanCipWatch(project.Id, new(project.CurrentSha256, Request().Tags)).UniqueLeafReads);
        Assert.Throws<OperationException>(() => service.PlanCipWatch(project.Id, new("stale", Request().Tags)));
        var saved = await service.SaveWatchAsync(Request());
        Assert.Equal("sdk_controller_disabled", (await Assert.ThrowsAsync<OperationException>(() => service.StartWatchAsync(saved.Definition.Id))).Code);
        Assert.Equal("sdk_target_policy", (await Assert.ThrowsAsync<OperationException>(() => service.SaveWatchAsync(Request() with { Tags = [new("v", "Enabled")] }))).Code);
        options.ControllerTargets = [options.ControllerTargets[0] with { CipRead = null }];
        Assert.Equal("cip_target", (await Assert.ThrowsAsync<OperationException>(() => service.SaveWatchAsync(Request()))).Code);
        Assert.Equal(0, reader.Opens);
    }
    [Fact]
    public async Task SelectedRepliesAreRecordedAndReaderFaultDoesNotReconnect()
    {
        options.EnableControllerOperations = true; reader.FailAfter = 1;
        var saved = await service.SaveWatchAsync(Request()); await service.StartWatchAsync(saved.Definition.Id);
        await Until(() => service.GetWatch(saved.Definition.Id).State == "faulted");
        var status = service.GetWatch(saved.Definition.Id); Assert.Equal(42, Assert.Single(status.Latest!.Values).Value!.GetValue<int>());
        Assert.True(status.Stale); Assert.Equal(1, reader.Opens); Assert.True(reader.Disposed);
        Assert.Equal("LibPlcTag", reader.Request!.Backend); Assert.Equal("192.0.2.1", reader.Request.CipTarget!.Gateway);
        Assert.Equal(Request().Tags, reader.Request.AllowedTags);
        Assert.Contains(service.QueryRecording(saved.Definition.Id, new()).Records, r => r.Kind == "fault");
    }
    [Fact]
    public async Task BadWorkerSelectionAndStaleSourceFailWithoutPublishing()
    {
        options.EnableControllerOperations = true; reader.BadScope = true;
        var saved = await service.SaveWatchAsync(Request()); await service.StartWatchAsync(saved.Definition.Id);
        await Until(() => service.GetWatch(saved.Definition.Id).State == "faulted");
        Assert.Null(service.GetWatch(saved.Definition.Id).Latest);
        reader.Opens = 0; File.AppendAllText(source, " "); await service.StartWatchAsync(saved.Definition.Id);
        await Until(() => service.GetWatch(saved.Definition.Id).State == "faulted"); Assert.Equal(0, reader.Opens);
    }
    [Fact]
    public void OldPersistedDefinitionsDefaultToSdkAndMalformedBackendCannotFallBack()
    {
        var bytes = JsonSerializer.SerializeToNode(Request(), ProjectService.Json)!.AsObject(); bytes.Remove("backend");
        Assert.Equal("Sdk", bytes.Deserialize<WatchListRequest>(ProjectService.Json)!.Backend);
        Assert.Throws<OperationException>(() => WatchValidation.Validate(Request() with { Backend = "unknown" }));
        Assert.Throws<OperationException>(() => WatchValidation.Validate(Request() with { Mode = "Offline" }));
        var tag = Request().Tags[0]; var good = new WatchValue(tag.Key, tag.TagName, tag.ProgramName, "DINT", JsonValue.Create(42), "good");
        Assert.Throws<OperationException>(() => CipWatchPlanning.ValidateValues([tag], [good, good]));
        Assert.Throws<OperationException>(() => CipWatchPlanning.ValidateValues([tag], [good with { Quality = "bad" }]));
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private sealed class FakeReader : IWatchSessionFactory, IWatchSession
    {
        public int Opens, FailAfter = int.MaxValue, Reads; public bool Disposed, BadScope; public WatchBridgeOpen? Request;
        public async Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct)
        { Opens++; Request = request; File.Copy(request.SourcePath, Path.Combine(request.WorkDirectory, "metadata.l5x")); await Task.Yield(); return this; }
        public Task<WatchBridgeReply> ReadAsync(WatchTag[] tags, CancellationToken ct)
        {
            if (++Reads > FailAfter) throw new IOException("Generated disconnect");
            var t = tags[0]; return Task.FromResult(new WatchBridgeReply(true, [], SelectedValues: [new(t.Key, t.TagName, BadScope ? "OtherProgram" : t.ProgramName, "DINT", JsonValue.Create(42), "good")]));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
