using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class MergeCompatibilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MergeCompatibility", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public MergeCompatibilityTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private async Task<ProjectInfo> Project(int count = 8, string text = "NOP();", int version = 35)
    {
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X")); doc.Root!.SetAttributeValue("SoftwareRevision", version + ".00"); doc.Root.Element("Controller")!.SetAttributeValue("MajorRev", version);
        LogixStructures.Tag(doc, "Record", "WatchProgram").Descendants("StructureMember").Single(e => (string?)e.Attribute("Name") == "Inner").Elements("DataValueMember").Single(e => (string?)e.Attribute("Name") == "Count").SetAttributeValue("Value", count);
        var program = doc.Descendants("Program").Single(); program.Element("Routines")?.Remove(); program.Add(XElement.Parse($"<Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Text>{text}</Text></Rung></RLLContent></Routine></Routines>"));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); doc.Save(path); return await service.RegisterAsync(path);
    }
    private static MergeSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.CurrentSha256);
    private static LogicValueMergeRequest Merge(ProjectInfo a, ProjectInfo b, ProjectInfo c, LogicValueResolution[]? resolutions = null) => new(Snapshot(a), Snapshot(b), Snapshot(c), [new("Record", "WatchProgram", "Record", "WatchProgram")], resolutions);
    [Fact]
    public async Task CommonBaselineMergesFreshValuesIntoModifiedLogicInANewPreservedCopy()
    {
        var a = await Project(); var b = await Project(text: "XIC(Enabled)NOP();"); var c = await Project(10); var request = Merge(a, b, c);
        var preview = await service.PreviewLogicValueMergeAsync(request); Assert.True(preview.CanApply); Assert.Equal(1, preview.CopiedValues);
        var merged = await service.ApplyLogicValueMergeAsync(new(request, preview.ReviewSha256)); Assert.DoesNotContain(merged.Id, new[] { a.Id, b.Id, c.Id });
        var extraction = service.ExtractStructures(new(ProjectId: merged.Id, TagName: "Record", ProgramName: "WatchProgram")); Assert.Equal(10L, extraction.Tags.Single().Value!["Inner"]!["Count"]!.GetValue<long>());
        Assert.Contains("XIC(Enabled)", service.ReadElement(merged.Id, "//Routine/RLLContent/Rung/Text").Xml);
        Assert.All(new[] { a, b, c }, p => { Assert.Equal(0, service.Get(p.Id).Revision); Assert.Equal(p.SourceSha256, ProjectService.Hash(File.ReadAllBytes(p.SourcePath))); });
    }
    [Fact]
    public async Task DivergentValuesRequireAnExplicitResolutionAndWrongReviewCannotApply()
    {
        var a = await Project(); var b = await Project(9, "XIC(Enabled)NOP();"); var c = await Project(10); var request = Merge(a, b, c);
        var conflict = await service.PreviewLogicValueMergeAsync(request); Assert.False(conflict.CanApply); Assert.Equal("conflict", conflict.Rows.Single().Status);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyLogicValueMergeAsync(new(request, conflict.ReviewSha256)));
        var resolved = request with { Resolutions = [new("Record", "WatchProgram", "fresh")] }; var review = await service.PreviewLogicValueMergeAsync(resolved); Assert.True(review.CanApply);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyLogicValueMergeAsync(new(resolved, conflict.ReviewSha256)));
        Assert.Equal("resolved-fresh", review.Rows.Single().Status);
    }
    [Fact]
    public async Task FreshSideLogicDivergenceRemainsBlockingEvenWithValueResolution()
    {
        var a = await Project(); var b = await Project(text: "XIC(Enabled)NOP();"); var c = await Project(10, "XIO(Enabled)NOP();");
        var preview = await service.PreviewLogicValueMergeAsync(Merge(a, b, c, [new("Record", "WatchProgram", "fresh")])); Assert.False(preview.CanApply); Assert.NotEmpty(preview.Conflicts);
    }
    [Fact]
    public async Task VersionPlanUsesNativeConversionAndRejectsDowngradeWithoutMakingJobs()
    {
        var p = await Project(); var plan = service.PreviewVersionCompatibility(new(p.Id, p.CurrentSha256, 38));
        Assert.True(plan.CanConvert); Assert.False(plan.NativeAvailable); Assert.Equal("ConvertAsync", plan.NativeRequest.Calls[0].Method); Assert.False(SdkCatalog.NeedsController(plan.NativeRequest.Calls));
        var lower = service.PreviewVersionCompatibility(new(p.Id, p.CurrentSha256, 34)); Assert.False(lower.CanConvert); Assert.Contains(lower.Issues, i => i.StartsWith("downgrade_unsupported"));
        await Assert.ThrowsAsync<OperationException>(() => service.StartVersionCompatibilityAsync(new(lower.Request, lower.ReviewSha256)));
        await Assert.ThrowsAsync<OperationException>(() => service.StartVersionCompatibilityAsync(new(plan.Request, plan.ReviewSha256))); Assert.Empty(service.SdkJobList()); Assert.Equal(0, service.Get(p.Id).Revision);
    }
    [Fact]
    public async Task NativeReadbackComparisonSeparatesVersionFieldsFromConfigurationAndDiagnostics()
    {
        var a = await Project(); var b = await Project(version: 38); var before = XDocument.Load(a.SourcePath); var after = XDocument.Load(b.SourcePath);
        after.Root!.Element("Controller")!.SetAttributeValue("NewOption", "Defaulted");
        var report = VersionCompatibility.CompareReadback(a, before, b, after, 38, [new("warning", "Unsupported option was defaulted by the native converter")]);
        Assert.True(report.TargetRevisionMatches); Assert.True(report.ConfigurationChanges > 0); Assert.Single(report.UnsupportedOrDefaultedMessages);
        after.Root.Element("Controller")!.SetAttributeValue("MajorRev", 37); Assert.False(VersionCompatibility.CompareReadback(a, before, b, after, 38, []).TargetRevisionMatches);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
