using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class AnnotationNormalizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellAnnotations", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public AnnotationNormalizationTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private static XDocument Source(int count = 1) => XDocument.Parse("<RSLogix5000Content SoftwareRevision='35.00'><Controller Name='Demo' MajorRev='35'><Tags>" +
        string.Concat(Enumerable.Range(0, count).Select(i => "<Tag Name='Tag" + i + "' TagType='Base' DataType='DINT'><Description><![CDATA[Start  internal\nline End]]></Description><Data Format='L5K'><![CDATA[1]]></Data></Tag>")) +
        "</Tags><Programs><Program Name='Program'><Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Comment><![CDATA[Rung  comment]]></Comment><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>", LoadOptions.PreserveWhitespace);
    private async Task<ProjectInfo> Register(XDocument doc)
    { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); File.WriteAllText(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(true)); return await service.RegisterAsync(path); }
    private static LogixValidationRequest Request(ProjectInfo candidate, ProjectInfo reference, bool enable = true) => new("native-readback", new(candidate.Id, candidate.CurrentSha256), Reference: new(reference.Id, reference.CurrentSha256), NativeRules: enable ? new(NormalizeAnnotationBoundaryWhitespace: true) : null);
    [Fact]
    public async Task Explicit_rule_retains_each_normalized_finding_and_exact_policy_and_receipt()
    {
        var source = Source(); var native = new XDocument(source); foreach (var e in native.Descendants().Where(e => e.Name.LocalName is "Description" or "Comment")) e.Value = " \t\r\n" + e.Value + "\r\n\t ";
        var reference = await Register(source); var candidate = await Register(native); var strict = await service.PreviewLogixValidationAsync(Request(candidate, reference, false)); Assert.Equal("fail", strict.Status);
        var request = Request(candidate, reference); var report = await service.PreviewLogixValidationAsync(request); Assert.Equal("pass", report.Status); Assert.Equal(2, report.Findings.Length);
        Assert.All(report.Findings, f => { Assert.Equal("annotation-boundary-whitespace", f.Check); Assert.Equal("normalized", f.Classification); Assert.Equal("pass", f.Status); Assert.NotEqual(f.ExpectedSha256, f.ActualSha256); Assert.Contains("  ", f.ExpectedValue); });
        Assert.NotEqual(strict.PolicySha256, report.PolicySha256); Assert.NotEqual(strict.ReviewSha256, report.ReviewSha256);
        await Assert.ThrowsAsync<OperationException>(() => service.RecordLogixValidationAsync(new(request, strict.ReviewSha256)));
        var receipt = await service.RecordLogixValidationAsync(new(request, report.ReviewSha256)); var retained = service.LogixValidationReceipt(receipt.Artifact.Id, receipt.Artifact.Sha256); Assert.Equal(report.ReviewSha256, retained.Report.ReviewSha256); Assert.True(retained.Report.Request.NativeRules!.NormalizeAnnotationBoundaryWhitespace);
        Assert.Equal(reference.CurrentSha256, ProjectService.Hash(File.ReadAllBytes(reference.SourcePath))); Assert.Equal(candidate.CurrentSha256, ProjectService.Hash(File.ReadAllBytes(candidate.SourcePath)));
    }
    [Theory] [InlineData("internal-space")] [InlineData("internal-newline")] [InlineData("unicode-boundary")] [InlineData("attribute")] [InlineData("mixed")] [InlineData("xml-comment")] [InlineData("stored-data")] [InlineData("logic")]
    public async Task Rule_cannot_hide_internal_text_attributes_markup_data_or_logic(string change)
    {
        var source = Source(); var native = new XDocument(source); var description = native.Descendants("Description").Single();
        switch (change)
        {
            case "internal-space": description.Value = " " + description.Value.Replace("  ", " ") + " "; break;
            case "internal-newline": description.Value = description.Value.Replace("\n", " "); break;
            case "unicode-boundary": description.Value = "\u00a0" + description.Value + "\u00a0"; break;
            case "attribute": description.Value = " " + description.Value; description.SetAttributeValue("Lang", "en"); break;
            case "mixed": description.Add(new XElement("Child", "retained")); break;
            case "xml-comment": description.Add(new XComment("new comment")); break;
            case "stored-data": source.Descendants("Data").Single().Add(new XElement("Description", "value")); native.Descendants("Data").Single().Add(new XElement("Description", " value ")); break;
            case "logic": native.Descendants("Text").Single().Value = " NOP(); "; break;
        }
        var reference = await Register(source); var candidate = await Register(native); var report = await service.PreviewLogixValidationAsync(Request(candidate, reference)); Assert.Equal("fail", report.Status); Assert.Contains(report.Findings, f => f.Status == "fail");
    }
    [Fact]
    public async Task Large_normalization_reports_do_not_consume_or_expand_exact_difference_waiver_budget()
    {
        var source = Source(1317); source.Descendants("Comment").Single().Remove(); var native = new XDocument(source);
        foreach (var e in native.Descendants("Description")) e.Value = " \t" + e.Value + "\n ";
        for (var i = 0; i < 354; i++) native.Descendants("Tag").ElementAt(i).SetAttributeValue("ExternalAccess", "None");
        var reference = await Register(source); var candidate = await Register(native); var strict = await service.PreviewLogixValidationAsync(Request(candidate, reference, false)); Assert.Equal(1671, strict.Findings.Count(f => f.Status == "fail"));
        var report = await service.PreviewLogixValidationAsync(Request(candidate, reference)); Assert.Equal("fail", report.Status); Assert.Equal(1317, report.Findings.Count(f => f.Classification == "normalized")); Assert.Equal(354, report.Findings.Count(f => f.Status == "fail"));
        var f = report.Findings.First(f => f.Status == "fail"); var waiver = new LogixExpectedDifference(f.Check, f.Location, f.ExpectedSha256, f.ActualSha256, "Generated budget check");
        var error = await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(Request(candidate, reference) with { ExpectedDifferences = Enumerable.Repeat(waiver, 1001).ToArray() })); Assert.Equal("validation_policy", error.Code);
        var receipt = await service.RecordLogixValidationAsync(new(Request(candidate, reference), report.ReviewSha256)); Assert.Equal("fail", receipt.Status);
    }
    [Fact]
    public async Task Rule_is_native_readback_only()
    {
        var p = await Register(Source()); var request = Request(p, p) with { Profile = "offline-integrity", Reference = null }; var error = await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(request)); Assert.Equal("validation_policy", error.Code);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
