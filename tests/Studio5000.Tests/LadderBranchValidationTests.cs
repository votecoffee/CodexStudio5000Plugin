using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LadderBranchValidationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellBranchGrammar", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public LadderBranchValidationTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private async Task<ProjectInfo> Register(string text, bool aoi = false, bool st = false)
    {
        var routine = new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", st ? "ST" : "RLL"),
            st ? new XElement("STContent", new XElement("Line", text)) : new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", new XCData(text)))));
        var container = aoi ? new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "Example"), new XElement("Routines", routine)))
            : new XElement("Programs", new XElement("Program", new XAttribute("Name", "Example"), new XAttribute("MainRoutineName", "Main"), new XElement("Routines", routine)));
        var doc = new XDocument(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Fixture"), container)));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); await File.WriteAllTextAsync(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return await service.RegisterAsync(path);
    }
    private static MergeSnapshot Snapshot(ProjectInfo project) => new(project.Id, project.CurrentSha256);

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Readable_program_and_AOI_rungs_report_the_exact_bad_branch_without_mutation(bool aoi)
    {
        var project = await Register("[[XIC(A)XIC(B)],XIC(C)]OTE(D);", aoi);
        var report = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(project)));
        Assert.Equal("fail", report.Status); var finding = Assert.Single(report.Findings, f => f.Check == LadderBranchGrammar.CheckName);
        Assert.Equal("fail", finding.Status); Assert.Equal("new", finding.Classification); Assert.Contains("own depth", finding.Detail);
        Assert.Contains("Rung[1]", finding.Location); Assert.NotNull(finding.ActualSha256);
        Assert.Equal(0, service.Get(project.Id).Revision); Assert.Empty(service.SdkJobList());
    }

    [Fact]
    public async Task Inherited_bad_grammar_still_fails_and_a_correction_is_reported_resolved()
    {
        var baseline = await Register("[XIC(A)]OTE(D);"); var same = await Register("[XIC(A)]OTE(D);");
        var inherited = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(same), Baseline: Snapshot(baseline)));
        Assert.Equal("fail", inherited.Status); Assert.Contains(inherited.Findings, f => f.Check == LadderBranchGrammar.CheckName && f.Classification == "inherited" && f.Status == "fail");
        var fixedProject = await Register("[XIC(A),XIC(B)]OTE(D);");
        var fixedReport = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(fixedProject), Baseline: Snapshot(baseline)));
        Assert.Equal("pass", fixedReport.Status); Assert.Contains(fixedReport.Findings, f => f.Check == LadderBranchGrammar.CheckName && f.Classification == "resolved");
    }

    [Fact]
    public async Task Native_expected_differences_cannot_waive_a_structural_branch_failure()
    {
        var project = await Register("[XIC(A),]OTE(D);");
        var request = new LogixValidationRequest("native-readback", Snapshot(project), Reference: Snapshot(project));
        var report = await service.PreviewLogixValidationAsync(request); var finding = Assert.Single(report.Findings, f => f.Check == LadderBranchGrammar.CheckName);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(request with
        { ExpectedDifferences = [new(LadderBranchGrammar.CheckName, finding.Location, null, finding.ActualSha256, "Cannot waive invalid grammar")] }));
        Assert.Equal("validation_policy", error.Code);
    }

    [Fact]
    public async Task Structured_text_is_outside_the_RLL_branch_check()
    {
        var project = await Register("// [not a ladder branch]\nValue := Array[1,2];", st: true);
        var report = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(project)));
        Assert.Equal("pass", report.Status); Assert.Equal(0, report.Checks.Single(c => c.Name == LadderBranchGrammar.CheckName).Compared);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
