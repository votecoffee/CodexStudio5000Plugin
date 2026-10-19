using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class WatchPreflightTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WatchPreflightTests", Guid.NewGuid().ToString("N"));
    private readonly string source, inert;
    private readonly ServiceOptions options;
    private readonly BombFactory factory = new();
    private readonly ProjectService service;
    public WatchPreflightTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "fixture.l5x");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"), source);
        inert = Path.Combine(root, "inert.exe"); File.WriteAllText(inert, "Not an executable. Preflight must never launch this file.");
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], EnableControllerOperations = true,
            NativeBridgePath = inert, CipBridgePath = inert,
            ControllerTargets = [new("lab", "configured-path", new(["GoOnlineAsync", "GetTagValueAsync"], "WatchProgram"), new("192.0.2.1", "1,0"))] };
        service = new(options, factory);
    }
    private WatchListRequest Request() => new("Preflight demo", [new("record", "Record", "WatchProgram"), new("count", "Record", "WatchProgram", "Inner.Count")],
        ProjectService.Hash(File.ReadAllBytes(source)), SourcePath: source, ControllerTargetId: "lab", IntervalMs: 1000, Backend: "LibPlcTag");
    private Dictionary<string, string> StateFiles() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("service.lock", StringComparison.Ordinal))
        .ToDictionary(p => p, p => ProjectService.Hash(File.ReadAllBytes(p)), StringComparer.Ordinal);
    [Fact]
    public async Task DraftInspectionIsReadOnlyAndDeduplicatesSupportedSymbolBudget()
    {
        var before = StateFiles(); var report = await service.PreflightWatchAsync(Request());
        Assert.Equal("local_checks_passed", report.Status); Assert.True(report.SourceHashMatches); Assert.Null(report.WatchId);
        Assert.Equal(6, report.Budget!.UniqueSupportedLeafReads); Assert.Equal(18, report.Budget.ScalarValueBytes);
        Assert.Equal(6d, report.Budget.NominalReadsPerSecond); Assert.True(report.Budget.Complete); Assert.True(report.Budget.FitsListLimits);
        Assert.Equal([6, 1], report.Selections.Select(s => s.ScalarReads)); Assert.All(report.Selections, s => Assert.Equal("supported", s.Status));
        Assert.Equal(before, StateFiles()); Assert.Empty(service.WatchLists()); Assert.Empty(service.SdkJobList()); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task ReportsEachUnsupportedSelectionWithoutDroppingGoodRowsOrCallingItComplete()
    {
        var request = Request() with { Tags = [new("ok", "Enabled", "WatchProgram"), new("string", "Message", "WatchProgram"), new("missing", "NotPresent", "WatchProgram"), new("member", "Record", "WatchProgram", "Missing"), new("index", "Records", "WatchProgram", "[2]")] };
        var report = await service.PreflightWatchAsync(request);
        Assert.Equal("blocked", report.Status); Assert.Equal(5, report.Selections.Length); Assert.Equal("supported", report.Selections[0].Status);
        Assert.All(report.Selections.Skip(1), s => { Assert.Equal("blocked", s.Status); Assert.NotEmpty(s.Issues); });
        Assert.Equal("cip_type_unsupported", report.Selections[1].Issues[0].Code);
        Assert.False(report.Budget!.Complete); Assert.Equal(1, report.Budget.UniqueSupportedLeafReads); Assert.Equal(1, report.Budget.SupportedSelections);
        Assert.Equal("deferred", report.Checks.Single(c => c.Name == "read_budget").Status); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task SourceChangesAreReportedAgainstObservedBytesAndNeverRebindSavedDefinition()
    {
        var saved = await service.SaveWatchAsync(Request()); var original = saved.Definition.Settings.ExpectedSha256; File.AppendAllText(source, " ");
        var before = StateFiles(); var report = await service.PreflightSavedWatchAsync(saved.Definition.Id);
        Assert.Equal(saved.Definition.Id, report.WatchId); Assert.Equal(0, report.WatchRevision); Assert.False(report.SourceHashMatches);
        Assert.NotEqual(original, report.SourceSha256); Assert.Equal("blocked", report.Status);
        Assert.Equal("stale_revision", report.Checks.Single(c => c.Name == "source_hash").Issue!.Code);
        Assert.Equal(original, service.GetWatch(saved.Definition.Id).Definition.Settings.ExpectedSha256); Assert.Equal("stopped", service.GetWatch(saved.Definition.Id).State);
        Assert.Equal(before, StateFiles()); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task DisabledGateMissingWorkerAndPolicyDenyAreReportedIndependently()
    {
        options.EnableControllerOperations = false; options.CipBridgePath = null;
        var report = await service.PreflightWatchAsync(Request() with { Tags = [new("outside", "Enabled"), new("inside", "Enabled", "WatchProgram")] });
        Assert.Contains(report.Checks, c => c.Issue?.Code == "sdk_controller_disabled"); Assert.Contains(report.Checks, c => c.Issue?.Code == "cip_unavailable");
        Assert.Equal("sdk_target_policy", report.Selections[0].Issues.Single().Code); Assert.Equal("supported", report.Selections[1].Status); Assert.Equal(0, factory.Opens);
        report = await service.PreflightWatchAsync(Request() with { ControllerTargetId = "unknown" });
        Assert.Contains(report.Checks, c => c.Issue?.Code == "sdk_target_unknown"); Assert.Equal("blocked", report.Status);
    }
    [Fact]
    public async Task CombinedLimitIsCheckedAcrossIndividuallySupportedSelections()
    {
        var doc = XmlProjects.Parse(File.ReadAllBytes(source)); var tags = doc.Descendants("Program").Single().Element("Tags")!;
        tags.Add(new XElement("Tag", new XAttribute("Name", "ArrayA"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "DINT"), new XAttribute("Dimensions", "200")));
        tags.Add(new XElement("Tag", new XAttribute("Name", "ArrayB"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "DINT"), new XAttribute("Dimensions", "100"))); doc.Save(source);
        var report = await service.PreflightWatchAsync(Request() with { Tags = [new("a", "ArrayA", "WatchProgram"), new("b", "ArrayB", "WatchProgram")] });
        Assert.All(report.Selections, s => Assert.Equal("supported", s.Status)); Assert.True(report.Budget!.Complete); Assert.False(report.Budget.FitsListLimits);
        Assert.Equal(300, report.Budget.UniqueSupportedLeafReads); Assert.Equal(1200, report.Budget.ScalarValueBytes); Assert.Equal("blocked", report.Status);
        Assert.Contains(report.Checks, c => c.Name == "read_budget" && c.Issue?.Code == "cip_plan_limit");
    }
    [Fact]
    public async Task DuplicateStructureSelectionsStillCountTowardCombinedNodeLimit()
    {
        var doc = XmlProjects.Parse(File.ReadAllBytes(source));
        var tag = doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Enabled"); tag.SetAttributeValue("Dimensions", "256"); doc.Save(source);
        var report = await service.PreflightWatchAsync(Request() with { Tags = Enumerable.Range(0, 8).Select(i => new WatchTag("v" + i, "Enabled", "WatchProgram")).ToArray() });
        Assert.Equal(256, report.Budget!.UniqueSupportedLeafReads); Assert.Equal(2056, report.Budget.SupportedPlanNodes); Assert.False(report.Budget.FitsListLimits);
    }
    [Fact]
    public async Task SdkStringsAreSupportedWithoutPretendingUnknownMemberTemplatesWereChecked()
    {
        var request = Request() with { Backend = "Sdk", Mode = "Offline", Tags = [new("text", "Message", "WatchProgram"), new("count", "Records", "WatchProgram", "[1].Inner.Count"), new("unknown", "Message", "WatchProgram", "DATA")] };
        var report = await service.PreflightWatchAsync(request);
        Assert.Null(report.Budget); Assert.Equal("incomplete", report.Status); Assert.Equal("STRING", report.Selections[0].DataType);
        Assert.Equal("DINT", report.Selections[1].DataType); Assert.Equal("deferred", report.Selections[2].Status);
        Assert.Equal("watch_declaration_unavailable", report.Selections[2].Issues.Single().Code); Assert.Equal(0, factory.Opens);
    }
    [Theory]
    [InlineData("Sdk", "incomplete")] [InlineData("LibPlcTag", "blocked")]
    public async Task AcdIsHashedWithoutLaunchingNativeInspection(string backend, string status)
    {
        var acd = Path.Combine(root, "opaque.acd"); File.WriteAllText(acd, "Generated inert ACD substitute; never open natively.");
        var report = await service.PreflightWatchAsync(Request() with { Backend = backend, SourcePath = acd, ExpectedSha256 = ProjectService.Hash(File.ReadAllBytes(acd)) });
        Assert.Equal(status, report.Status); Assert.True(report.SourceHashMatches); Assert.All(report.Selections, s => Assert.Equal("deferred", s.Status)); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task InvalidSelectorsAndInvalidXmlDoNotHideOtherDiagnostics()
    {
        var report = await service.PreflightWatchAsync(Request() with { Tags = [new("bad", "Bad&name=Injection", "WatchProgram"), new("path", "Record", "WatchProgram", "Inner[-1]"), new("ok", "Enabled", "WatchProgram")] });
        Assert.Equal("watch_tag", report.Selections[0].Issues.Single().Code); Assert.Equal("watch_member", report.Selections[1].Issues.Single().Code); Assert.Equal("supported", report.Selections[2].Status);
        File.WriteAllText(source, "<!DOCTYPE x [<!ENTITY injected SYSTEM 'file:///not-read'>]><x>&injected;</x>");
        report = await service.PreflightWatchAsync(Request()); Assert.Equal("blocked", report.Status);
        Assert.Contains(report.Checks, c => c.Name == "metadata" && c.Status == "blocked"); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task PathsOutsideAllowedRootsAndInvalidListBoundsRemainRequestErrors()
    {
        var path = Path.Combine(Path.GetTempPath(), "outside.l5x");
        Assert.Equal("path_denied", (await Assert.ThrowsAsync<OperationException>(() => service.PreflightWatchAsync(Request() with { SourcePath = path }))).Code);
        await Assert.ThrowsAsync<OperationException>(() => service.PreflightWatchAsync(Request() with { Tags = [] }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PreflightWatchAsync(Request(), cancelled.Token)); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task BrowserPreflightUsesHashBoundManagedRevisionWithoutCreatingAnArtifactOrWatch()
    {
        var project = await service.RegisterAsync(source); File.WriteAllText(source, "External source changed after registration.");
        var before = StateFiles();
        var browser = new BrowserWatchRequest(project.Id, project.CurrentSha256, "Browser draft", [new("ok", "Enabled", "WatchProgram"), new("bad", "Message", "WatchProgram")],
            "Online", "lab", Backend: "LibPlcTag");
        var report = await service.PreflightWatchAsync(new WatchPreflightRequest(Browser: browser));
        Assert.True(report.SourceHashMatches); Assert.Equal(project.CurrentSha256, report.SourceSha256); Assert.Equal("supported", report.Selections[0].Status);
        Assert.Equal("cip_type_unsupported", report.Selections[1].Issues.Single().Code); Assert.Equal(before, StateFiles()); Assert.Empty(service.WatchLists());
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PreflightWatchAsync(new WatchPreflightRequest(Browser: browser with { ExpectedSha256 = "stale" })))).Code);
        await Assert.ThrowsAsync<OperationException>(() => service.PreflightWatchAsync(new WatchPreflightRequest()));
        await Assert.ThrowsAsync<OperationException>(() => service.PreflightWatchAsync(new WatchPreflightRequest(Settings: Request(), Browser: browser)));
        Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task MalformedDeclarationsProduceBoundedDiagnosticsInsteadOfNativeExecution()
    {
        var doc = XmlProjects.Parse(File.ReadAllBytes(source)); doc.Descendants("Member").First().Attribute("DataType")!.Remove(); doc.Save(source);
        var report = await service.PreflightWatchAsync(Request());
        Assert.Equal("blocked", report.Status); Assert.Contains(report.Checks, c => c.Issue?.Code == "watch_declaration_invalid");
        Assert.All(report.Selections, row => Assert.Equal("deferred", row.Status)); Assert.Equal(0, factory.Opens);
        doc = XmlProjects.Parse(File.ReadAllBytes(source)); doc.Descendants("Member").First().SetAttributeValue("DataType", "DINT");
        doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Record").SetAttributeValue("DataType", new string('x', 10000)); doc.Save(source);
        report = await service.PreflightWatchAsync(Request()); Assert.All(report.Selections, row => Assert.Equal("watch_declaration_invalid", row.Issues.Single().Code));
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report).Length < 10000);
    }
    private sealed class BombFactory : IWatchSessionFactory
    {
        public int Opens;
        public Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct) { Opens++; throw new InvalidOperationException("Preflight opened a reader."); }
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
