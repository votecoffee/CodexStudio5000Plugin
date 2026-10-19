using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class TagValueMergeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ValueMergeTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public TagValueMergeTests()
    {
        Directory.CreateDirectory(root);
        service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] });
    }
    private async Task<ProjectInfo> Project(Action<XDocument>? edit = null)
    {
        var doc = XmlProjects.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"))); edit?.Invoke(doc);
        var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); doc.Save(file); return await service.RegisterAsync(file);
    }
    private static TagValueMergeRequest Request(ProjectInfo a, ProjectInfo b, params string[] tags) =>
        new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, tags.Length == 0 ? null : tags.Select(t => new TagValueMap(t, "WatchProgram", t, "WatchProgram")).ToArray(), "WatchProgram", "WatchProgram");
    private static XElement Tag(XDocument doc, string name) => LogixStructures.Tag(doc, name, "WatchProgram");
    private XDocument Managed(ProjectInfo project) => XDocument.Parse(service.ReadElement(project.Id, "/RSLogix5000Content").Xml);

    [Fact]
    public async Task MergeCopiesNestedArraysScalarsAndStringsButPreservesDefinitionsLogicForcesAndOriginals()
    {
        var source = await Project(); var target = await Project(doc =>
        {
            Tag(doc, "Record").Descendants("DataValueMember").First().SetAttributeValue("Value", "99");
            Tag(doc, "Record").Add(new XElement("Description", "Destination description"), new XElement("ForceData", "preserved"));
            Tag(doc, "Enabled").Descendants("DataValue").Single().SetAttributeValue("Value", "0");
            Tag(doc, "Message").Element("Data")!.ReplaceNodes(new XCData("'Other value'"));
            Tag(doc, "Records").Descendants("Element").First().Descendants("DataValueMember").First().SetAttributeValue("Value", "123");
        });
        var files = new[] { source.SourcePath, target.SourcePath }.ToDictionary(p => p, p => ProjectService.Hash(File.ReadAllBytes(p)));
        var before = Managed(target); var request = Request(source, target); var preview = await service.PreviewTagValueMergeAsync(request);
        Assert.True(preview.CanApply); Assert.Equal(4, preview.Changed); Assert.Equal(target, service.Get(target.Id));
        var merged = await service.ApplyTagValueMergeAsync(new(request, preview.ReviewSha256)); var after = Managed(merged);
        Assert.Equal(1, merged.Revision); Assert.Equal(preview.ResultSha256, merged.CurrentSha256); Assert.Equal(source, service.Get(source.Id));
        Assert.Equal("Destination description", Tag(after, "Record").Element("Description")!.Value); Assert.Equal("preserved", Tag(after, "Record").Element("ForceData")!.Value);
        foreach (var name in new[] { "Record", "Records", "Enabled", "Message" })
        {
            var sourceValue = service.ExtractStructures(new(ProjectId: source.Id, TagName: name)).Tags.Single().Value;
            var resultValue = service.ExtractStructures(new(ProjectId: merged.Id, TagName: name)).Tags.Single().Value;
            Assert.Equal(sourceValue!.ToJsonString(), resultValue!.ToJsonString()); Assert.Single(Tag(after, name).Elements("Data"));
        }
        foreach (var data in before.Descendants("Tag").Elements("Data").ToArray()) data.Remove();
        foreach (var data in after.Descendants("Tag").Elements("Data").ToArray()) data.Remove();
        Assert.True(XNode.DeepEquals(before, after)); Assert.All(files, p => Assert.Equal(p.Value, ProjectService.Hash(File.ReadAllBytes(p.Key))));
        Assert.Contains(service.History(merged.Id), e => e.Action == "value_merge");
    }
    [Fact]
    public async Task ExplicitTagAndProgramMappingChangesOnlyTheSelectedDestination()
    {
        var a = await Project(); var b = await Project(doc =>
        {
            doc.Descendants("Program").Single().SetAttributeValue("Name", "OtherProgram");
            TagIn(doc, "OtherProgram", "Enabled").SetAttributeValue("Name", "MappedEnable");
            TagIn(doc, "OtherProgram", "MappedEnable").Descendants("DataValue").Single().SetAttributeValue("Value", "0");
        });
        var request = Request(a, b) with { Tags = [new("Enabled", "WatchProgram", "MappedEnable", "OtherProgram")] };
        var p = await service.PreviewTagValueMergeAsync(request); Assert.True(p.CanApply);
        var result = await service.ApplyTagValueMergeAsync(new(request, p.ReviewSha256));
        Assert.Equal("1", TagIn(Managed(result), "OtherProgram", "MappedEnable").Descendants("DataValue").Single().Attribute("Value")!.Value);
        static XElement TagIn(XDocument doc, string program, string name) => LogixStructures.Tag(doc, name, program);
    }
    [Fact]
    public async Task IncompatibleUdtsDimensionsAliasesSafetyAndMissingTagsBlockTheWholeRequest()
    {
        var a = await Project();
        foreach (var change in new Action<XDocument>[] {
            d => d.Descendants("Member").First().SetAttributeValue("DataType", "LINT"),
            d => Tag(d, "Record").SetAttributeValue("Dimensions", "2"),
            d => Tag(d, "Record").SetAttributeValue("TagType", "Alias"),
            d => Tag(d, "Record").SetAttributeValue("Class", "Safety"),
            d => Tag(d, "Record").Remove() })
        {
            var b = await Project(change); var request = Request(a, b, "Record", "Enabled"); var preview = await service.PreviewTagValueMergeAsync(request);
            Assert.False(preview.CanApply); Assert.NotNull(preview.Rows[0].Message);
            Assert.Equal("merge_blocked", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyTagValueMergeAsync(new(request, preview.ReviewSha256)))).Code);
            Assert.Equal(b, service.Get(b.Id));
        }
    }
    [Theory]
    [InlineData("DINT", "2147483648")]
    [InlineData("BOOL", "2")]
    [InlineData("REAL", "NaN")]
    [InlineData("REAL", "1e300")]
    public async Task MalformedValuesAreReportedAndCannotBeApplied(string type, string value)
    {
        var a = await Project(doc => { var tag = Tag(doc, "Enabled"); tag.SetAttributeValue("DataType", type); tag.Descendants("DataValue").Single().SetAttributeValue("DataType", type); tag.Descendants("DataValue").Single().SetAttributeValue("Value", value); });
        var b = await Project(doc => { Tag(doc, "Enabled").SetAttributeValue("DataType", type); Tag(doc, "Enabled").Descendants("DataValue").Single().SetAttributeValue("DataType", type); });
        var p = await service.PreviewTagValueMergeAsync(Request(a, b, "Enabled")); Assert.False(p.CanApply); Assert.Equal("merge_value", p.Rows.Single().Status);
    }
    [Fact]
    public async Task RawOnlyIncompleteArraysAndDuplicateCoordinatesAreNotMerged()
    {
        var b = await Project();
        foreach (var edit in new Action<XDocument>[] {
            d => Tag(d, "Record").Elements("Data").Where(e => (string?)e.Attribute("Format") == "Decorated").Remove(),
            d => Tag(d, "Record").Descendants("ArrayMember").Single().Elements().Last().Remove(),
            d => Tag(d, "Record").Descendants("ArrayMember").Single().Elements().Last().SetAttributeValue("Index", "[00]") })
        { var a = await Project(edit); var p = await service.PreviewTagValueMergeAsync(Request(a, b, "Record")); Assert.False(p.CanApply); }
    }
    [Fact]
    public async Task StaleSourceDestinationReviewAndDuplicateTargetsAreRejectedWithoutChangingTheDestination()
    {
        var a = await Project(); var b = await Project(); var request = Request(a, b, "Record"); var preview = await service.PreviewTagValueMergeAsync(request);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyTagValueMergeAsync(new(request, "wrong")))).Code);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewTagValueMergeAsync(request with { SourceSha256 = "old" }))).Code);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewTagValueMergeAsync(request with { DestinationSha256 = "old" }))).Code);
        Assert.Equal("merge_duplicate", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewTagValueMergeAsync(request with { Tags = [request.Tags![0], request.Tags[0]] }))).Code);
        Assert.Equal("merge_same_project", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewTagValueMergeAsync(request with { DestinationProjectId = a.Id }))).Code);
        var edit = await service.PreviewAsync(a.Id, new(a.CurrentSha256, [new("set_attribute", "//Tag[@Name='Enabled']/Data/DataValue", "0", "Value")]));
        await service.ApplyAsync(a.Id, edit.Id, new(a.CurrentSha256));
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyTagValueMergeAsync(new(request, preview.ReviewSha256)))).Code); Assert.Equal(b, service.Get(b.Id));
    }
    [Fact]
    public async Task OnlineCaptureHonorsDisabledControllerGateWithoutLaunchingTheBridge()
    {
        var bridge = Path.Combine(root, "unlaunchable.exe"); File.WriteAllText(bridge, "not executable");
        using var isolated = new ProjectService(new() { StateDirectory = Path.Combine(root, "gated"), AllowedRoots = [root], NativeBridgePath = bridge,
            ControllerTargets = [new("fixture", "inert-route")] });
        Assert.Equal("sdk_controller_disabled", (await Assert.ThrowsAsync<OperationException>(() => isolated.CaptureTagValueSourceAsync(new("fixture")))).Code);
        Assert.Empty(isolated.SdkJobList());
    }
    [Fact]
    public async Task UploadProvenanceRequiresTheUneditedExactUploadResult()
    {
        var a = await Project(); var b = await Project(); var jobId = new string('f', 32);
        var uploaded = a with { SourcePath = "sdk-job:" + jobId };
        File.WriteAllText(Path.Combine(root, "state", "projects", a.Id, "project.json"), JsonSerializer.Serialize(uploaded, ProjectService.Json));
        var dir = Path.Combine(root, "state", "sdk-jobs", jobId); Directory.CreateDirectory(dir);
        void Job(string[] methods) => File.WriteAllText(Path.Combine(dir, "job.json"), JsonSerializer.Serialize(new SdkJob(jobId, "succeeded", DateTimeOffset.UnixEpoch, methods, null, "fixture", true, [], [], [], uploaded), ProjectService.Json));
        Job(["UploadToNewProjectAsync"]); var p = await service.PreviewTagValueMergeAsync(Request(uploaded, b, "Record")); Assert.Equal("controller_upload_snapshot", p.Origin); Assert.NotNull(p.CapturedAt);
        Job(["UploadToNewProjectAsync", "SetTagValueDINTAsync"]); p = await service.PreviewTagValueMergeAsync(Request(uploaded, b, "Record")); Assert.Equal("saved_project", p.Origin); Assert.Null(p.CapturedAt);
    }
    [Fact]
    public async Task BrowseLargeScopesSearchesAndPagesWithoutPreviewingAllTagsOrChangingFiles()
    {
        void Many(XDocument d, string value) => d.Root!.Element("Controller")!.Element("Tags")!.Add(Enumerable.Range(0, 310).Select(i =>
            new XElement("Tag", new XAttribute("Name", $"Tag{i:D3}"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "DINT"),
                new XElement("Data", new XAttribute("Format", "Decorated"), new XElement("DataValue", new XAttribute("DataType", "DINT"), new XAttribute("Value", value))))));
        var a = await Project(d => Many(d, "42")); var b = await Project(d => Many(d, "0"));
        Dictionary<string, string> Files() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "service.lock").ToDictionary(p => p, p => ProjectService.Hash(File.ReadAllBytes(p)));
        var before = Files();
        var request = new TagValueMergeBrowseRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, Limit: 100);
        var first = await service.BrowseTagValueMergeAsync(request); var next = await service.BrowseTagValueMergeAsync(request with { Offset = 100 });
        Assert.Equal(310, first.Total); Assert.Equal(100, first.Rows.Length); Assert.Equal("Tag000", first.Rows[0].Tag.SourceTag); Assert.Equal("Tag100", next.Rows[0].Tag.SourceTag);
        var search = await service.BrowseTagValueMergeAsync(request with { Query = "Tag2" }); Assert.Equal(100, search.Total); Assert.All(search.Rows, r => Assert.StartsWith("Tag2", r.Tag.SourceTag));
        Assert.Empty((await service.BrowseTagValueMergeAsync(request with { Offset = 400 })).Rows);
        Assert.Equal(before, Files());
        var previewRequest = new TagValueMergeRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, [first.Rows[0].Tag, next.Rows[0].Tag]);
        var preview = await service.PreviewTagValueMergeAsync(previewRequest); Assert.True(preview.CanApply); var result = await service.ApplyTagValueMergeAsync(new(previewRequest, preview.ReviewSha256));
        var values = service.ExtractStructures(new(ProjectId: result.Id, Offset: 0, Limit: 256));
        Assert.Equal(42, values.Tags.Single(t => t.Name == "Tag000").Value!.GetValue<long>()); Assert.Equal(42, values.Tags.Single(t => t.Name == "Tag100").Value!.GetValue<long>()); Assert.Equal(0, values.Tags.Single(t => t.Name == "Tag101").Value!.GetValue<long>());
    }
    [Fact]
    public async Task BrowseRetainsIndividualDiagnosticsAndHonorsExactScopes()
    {
        var a = await Project(); var b = await Project(d => Tag(d, "Enabled").Remove());
        var request = new TagValueMergeBrowseRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, "WatchProgram", "WatchProgram");
        var page = await service.BrowseTagValueMergeAsync(request); Assert.Equal(4, page.Total); Assert.Equal("structure_tag_missing", page.Rows.Single(r => r.Tag.SourceTag == "Enabled").Status);
        Assert.Equal(2, (await service.BrowseTagValueMergeAsync(request with { Query = "Record" })).Total);
        Assert.Equal(1, (await service.BrowseTagValueMergeAsync(request with { Query = "bool" })).Total);
        Assert.Empty((await service.BrowseTagValueMergeAsync(request with { SourceProgram = null })).Rows);
    }
    [Fact]
    public async Task BrowseRejectsStaleAndInvalidRequestsBeforeChangingState()
    {
        var a = await Project(); var b = await Project(); var request = new TagValueMergeBrowseRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256);
        foreach (var changed in new[] { request with { Limit = 101 }, request with { Offset = -1 }, request with { Query = new string('x', 257) } })
            Assert.Equal("merge_browse_query", (await Assert.ThrowsAsync<OperationException>(() => service.BrowseTagValueMergeAsync(changed))).Code);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.BrowseTagValueMergeAsync(request with { SourceSha256 = "old" }))).Code);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.BrowseTagValueMergeAsync(request with { DestinationSha256 = "old" }))).Code);
        Assert.Equal("merge_same_project", (await Assert.ThrowsAsync<OperationException>(() => service.BrowseTagValueMergeAsync(request with { DestinationProjectId = a.Id }))).Code);
        Assert.Equal("watch_tag", (await Assert.ThrowsAsync<OperationException>(() => service.BrowseTagValueMergeAsync(request with { DestinationProgram = "bad/program" }))).Code);
        Assert.Equal(a, service.Get(a.Id)); Assert.Equal(b, service.Get(b.Id));
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
