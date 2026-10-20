using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LegacyReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellLegacyReview", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public LegacyReviewTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private static string L5K => "(* Native exporter v35.03; μ 日本語; IE_VER := 9.99; MODULE Local (Major := 99) *)\r\nIE_VER := 2.26;\r\nCONTROLLER Demo (Description := \"Keep $\"MODULE Local (Major := 35)$\" and $N text\",\r\n Major := 35, AutoDiagsEnabled := 1, WebServerEnabled := 0, Keep := 9)\r\n DATATYPE Example (FamilyType := NoFamily)\r\n END_DATATYPE\r\n MODULE Local (CatalogNumber := \"DemoController\", Major := 35, Minor := 11)\r\n END_MODULE\r\n MODULE Remote (Major := 35)\r\n END_MODULE\r\n PROGRAM Example\r\n TAG Value : DINT := 35; END_TAG\r\n END_PROGRAM\r\nEND_CONTROLLER\r\n";
    private static XDocument Document(int major = 35)
    {
        var protectedAoi = new XElement("EncodedData", new XAttribute("Name", "ProtectedDemo"), new XAttribute("EncodedType", "AddOnInstructionDefinition"), new XAttribute("SoftwareRevision", "v19"), new XElement("Parameters"), new XCData("\r\nYWJjZA==\r\n"));
        var aoi = new XElement("AddOnInstructionDefinition", new XAttribute("Name", "ReadableDemo"), new XElement("Parameters", new XElement("Parameter", new XAttribute("Name", "Count"), new XAttribute("DataType", "DINT"), new XElement("DefaultData", new XAttribute("Format", "L5K"), "7"))), new XElement("LocalTags"), new XElement("Routines", Routine("Logic")));
        var tags = new XElement("Tags", new XElement("Tag", new XAttribute("Name", "Value"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "DINT"), new XElement("Data", new XAttribute("Format", "L5K"), new XCData("35"))),
            new XElement("Tag", new XAttribute("Name", "Axis"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "AXIS_VIRTUAL"), new XElement("Data", new XAttribute("Format", "Axis"), new XElement("AxisParameters", new XAttribute("MaximumSpeed", "10")))));
        var programs = new XElement("Programs", new XElement("Program", new XAttribute("Name", "DemoProgram"), new XAttribute("MainRoutineName", "Main"), new XElement("Routines", Routine("Main"))));
        var controller = new XElement("Controller", new XAttribute("Name", "Demo"), new XAttribute("MajorRev", major), new XElement("Modules", new XElement("Module", new XAttribute("Name", "Local"), new XAttribute("Major", major))), new XElement("DataTypes"), new XElement("AddOnInstructionDefinitions", protectedAoi, aoi), tags, programs);
        return new(new XElement("RSLogix5000Content", new XAttribute("SoftwareRevision", major + ".03"), controller));
    }
    private static XElement Routine(string name) => new("Routine", new XAttribute("Name", name), new XAttribute("Type", "RLL"), new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "0"), new XElement("Comment", "Keep annotation"), new XElement("Text", new XCData("NOP();")))));
    private async Task<ProjectInfo> Register(XDocument doc)
    { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".L5X"); File.WriteAllText(path, doc.ToString()); return await service.RegisterAsync(path); }
    private static MergeSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.CurrentSha256);
    private void SaveJob(SdkJob job)
    { var dir = Path.Combine(root, "state", "sdk-jobs", job.Id); Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "job.json"), JsonSerializer.SerializeToUtf8Bytes(job, ProjectService.Json)); }
    private async Task<(LegacyReviewRequest Request, SdkJob Job, byte[] Bytes)> Export(string? text = null, bool bom = true)
    {
        var source = await Register(Document()); var bytes = new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(text ?? L5K)).ToArray();
        var artifactId = Guid.NewGuid().ToString("N"); var dir = Path.Combine(root, "state", "artifacts", artifactId); Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "project.l5k"), bytes);
        var artifact = new Artifact(artifactId, source.Id, "l5k", ProjectService.Hash(bytes), bytes.Length, "Generated offline test fixture; no native acceptance claimed."); File.WriteAllBytes(Path.Combine(dir, "artifact.json"), JsonSerializer.SerializeToUtf8Bytes(artifact, ProjectService.Json));
        var job = new SdkJob(Guid.NewGuid().ToString("N"), "succeeded", DateTimeOffset.UnixEpoch, ["SaveAsAsync"], new('a', 64), null, false, [], [], [artifact], source); SaveJob(job);
        return (new(job.Id, artifact.Id, artifact.Sha256, "Generated review test"), job, bytes);
    }
    [Fact]
    public async Task Exact_preparation_preserves_values_comments_encoding_and_native_source_with_no_jobs()
    {
        var input = await Export(); var plan = await service.PreviewLegacyReviewAsync(input.Request);
        Assert.True(plan.ReviewOnly); Assert.Equal(5, plan.Edits.Length); Assert.Equal(32, plan.TargetRevision); Assert.Contains("No native job", plan.Boundary);
        var prepared = await service.PrepareLegacyReviewAsync(new(input.Request, plan.ReviewSha256)); var bytes = File.ReadAllBytes(service.Download(prepared.Input.Id).Path);
        var expected = L5K.Replace("IE_VER := 2.26;\r\nCONTROLLER", "IE_VER := 2.23;\r\nCONTROLLER").Replace(" Major := 35, AutoDiagsEnabled := 1, WebServerEnabled := 0, Keep := 9", " Major := 32, Keep := 9").Replace("CatalogNumber := \"DemoController\", Major := 35", "CatalogNumber := \"DemoController\", Major := 32");
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(expected)), bytes); Assert.Equal(plan.PreparedSha256, ProjectService.Hash(bytes));
        Assert.Equal(input.Bytes, File.ReadAllBytes(service.Download(input.Request.L5kArtifactId).Path)); Assert.Single(service.SdkJobList()); Assert.Null(prepared.ImportRequest.ControllerTargetId);
        Assert.Equal(prepared.Input.Id, prepared.ImportRequest.SourceArtifactId); Assert.Equal(prepared.Input.Sha256, prepared.ImportRequest.ExpectedSha256); Assert.Equal("SaveAsync", Assert.Single(prepared.ImportRequest.Calls).Method);
        var manifest = JsonSerializer.Deserialize<LegacyReviewManifest>(File.ReadAllBytes(service.Download(prepared.Manifest.Id).Path), ProjectService.Json)!;
        Assert.Equal("legacy_review_input", manifest.Kind); Assert.Equal(plan.ReviewSha256, manifest.Plan.ReviewSha256); Assert.StartsWith("pending", prepared.NativeAcceptance);
    }
    [Theory]
    [InlineData("running", false)] [InlineData("failed", false)] [InlineData("indeterminate", false)] [InlineData("succeeded", true)]
    public async Task Incomplete_or_controller_effect_jobs_cannot_be_native_export_provenance(string status, bool controllerEffects)
    {
        var input = await Export(); SaveJob(input.Job with { Status = status, ControllerEffectsPossible = controllerEffects });
        Assert.Equal("legacy_review_provenance", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewLegacyReviewAsync(input.Request))).Code);
    }
    [Theory]
    [InlineData("IE_VER := 2.26;", "IE_VER := 2.27;")]
    [InlineData("MODULE Local (", "MODULE Elsewhere (")]
    [InlineData("Major := 35, AutoDiagsEnabled", "Major := 34, AutoDiagsEnabled")]
    [InlineData("AutoDiagsEnabled := 1", "AutoDiagsEnabled := Maybe")]
    [InlineData("END_CONTROLLER", "MODULE Local (Major := 35)\r\nEND_MODULE\r\nEND_CONTROLLER")]
    [InlineData("END_CONTROLLER", "END_CONTROLLER\r\nCONTROLLER Other (Major := 35)")]
    public async Task Unsupported_or_ambiguous_inputs_never_produce_prepared_artifacts(string before, string after)
    {
        var input = await Export(L5K.Replace(before, after)); var original = Directory.GetDirectories(Path.Combine(root, "state", "artifacts")).Length;
        Assert.Equal("legacy_review_unsupported", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewLegacyReviewAsync(input.Request))).Code);
        Assert.Equal(original, Directory.GetDirectories(Path.Combine(root, "state", "artifacts")).Length);
    }
    [Fact]
    public async Task Review_hash_profile_artifact_metadata_and_source_hash_are_rechecked()
    {
        var input = await Export(); var plan = await service.PreviewLegacyReviewAsync(input.Request);
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareLegacyReviewAsync(new(input.Request with { Purpose = "Changed purpose" }, plan.ReviewSha256)));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewLegacyReviewAsync(input.Request with { Profile = "v38-to-v31" }));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewLegacyReviewAsync(input.Request with { ExpectedSha256 = new('b', 64) }));
        File.AppendAllText(service.Download(input.Request.L5kArtifactId).Path, "tampered");
        Assert.Equal("artifact_tampered", (await Assert.ThrowsAsync<OperationException>(() => service.PrepareLegacyReviewAsync(new(input.Request, plan.ReviewSha256)))).Code);
    }
    [Fact]
    public async Task Leading_omissions_and_no_BOM_keep_all_unselected_bytes()
    {
        var text = L5K.Replace("CONTROLLER Demo (Description", "CONTROLLER Demo (AutoDiagsEnabled := 1, WebServerEnabled := 0, Description").Replace(", AutoDiagsEnabled := 1, WebServerEnabled := 0, Keep", ", Keep").Replace("\r\n", "\n");
        var input = await Export(text, false); var plan = await service.PreviewLegacyReviewAsync(input.Request); var result = await service.PrepareLegacyReviewAsync(new(input.Request, plan.ReviewSha256));
        var bytes = File.ReadAllBytes(service.Download(result.Input.Id).Path); Assert.False(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
        Assert.Contains("CONTROLLER Demo (Description", Encoding.UTF8.GetString(bytes)); Assert.DoesNotContain("\r", Encoding.UTF8.GetString(bytes));
    }
    [Fact]
    public async Task Legacy_readback_separates_motion_and_protected_envelope_from_code_values_and_complete_payload()
    {
        var source = await Register(Document()); var after = Document(32);
        after.Descendants("AxisParameters").Single().SetAttributeValue("MaximumSpeed", "9"); after.Descendants("EncodedData").Single().SetAttributeValue("SoftwareRevision", "v32");
        var payload = after.Descendants("EncodedData").Single().Nodes().OfType<XText>().Single(); payload.Value = "\nYWJj\nZA==\n";
        var candidate = await Register(after); var request = new LogixValidationRequest("legacy-review-readback", Snapshot(candidate), Reference: Snapshot(source), NativeRules: new(NormalizeProtectedPayloadWhitespace: true));
        var report = await service.PreviewLogixValidationAsync(request);
        Assert.Contains(report.Checks, c => c.Name == "target-version" && c.Status == "pass"); Assert.Contains(report.Checks, c => c.Name == "ordinary-tag-values" && c.Status == "pass");
        Assert.Contains(report.Checks, c => c.Name == "readable-routine-code" && c.Compared == 2 && c.Status == "pass"); Assert.Contains(report.Checks, c => c.Name == "protected-aoi-payload" && c.Status == "pass");
        Assert.Contains(report.Findings, f => f.Check == "motion-configuration"); Assert.Contains(report.Findings, f => f.Check == "protected-aoi-envelope" && f.Location.EndsWith("/@SoftwareRevision")); Assert.StartsWith("REVIEW ONLY", report.Boundary);
        var expected = report.Findings.Where(f => f.Status == "fail" && f.Classification == "new").Select(f => new LogixExpectedDifference(f.Check, f.Location, f.ExpectedSha256, f.ActualSha256, "Exact generated metadata/default review")).ToArray();
        var reviewed = request with { ExpectedDifferences = expected }; var accepted = await service.PreviewLogixValidationAsync(reviewed); Assert.Equal("pass", accepted.Status);
        var receipt = await service.RecordLogixValidationAsync(new(reviewed, accepted.ReviewSha256)); Assert.Equal("legacy-review-readback", service.LogixValidationReceipt(receipt.Artifact.Id, receipt.Artifact.Sha256).Report.Profile);
        after.Descendants("EncodedData").Single().Nodes().OfType<XText>().Single().Value = "YWJjZQ=="; var corrupt = await Register(after);
        var failed = await service.PreviewLogixValidationAsync(request with { Candidate = Snapshot(corrupt) }); Assert.Contains(failed.Findings, f => f.Check == "protected-aoi-payload" && f.Status == "fail");
        Assert.DoesNotContain(failed.Findings.Where(f => f.Check == "protected-aoi-payload"), f => f.ExpectedValue is not null || f.ActualValue is not null);
        var change = failed.Findings.First(f => f.Check == "protected-aoi-payload");
        var opaqueReviewed = await service.PreviewLogixValidationAsync(request with { Candidate = Snapshot(corrupt), ExpectedDifferences = [new(change.Check, change.Location, change.ExpectedSha256, change.ActualSha256, "Explicit generated byte difference; opaque semantics remain uninspected")] });
        Assert.Contains(opaqueReviewed.Findings, f => f.Check == "protected-aoi-payload" && f.Classification == "expected"); Assert.Contains(opaqueReviewed.UnsupportedCoverage, s => s.Contains("opaque-code equivalence"));
    }
    [Theory]
    [InlineData("value", "ordinary-tag-values")] [InlineData("code", "readable-routine-code")] [InlineData("missing-tag", "ordinary-tag-layout")] [InlineData("default", "aoi-default-values")]
    public async Task Code_and_ordinary_value_or_layout_changes_cannot_be_approved_as_metadata(string kind, string check)
    {
        var source = await Register(Document()); var after = Document(32);
        if (kind == "value") after.Descendants("Tag").First().Element("Data")!.Value = "999";
        if (kind == "code") after.Descendants("Text").First().Value = "AFI();";
        if (kind == "missing-tag") after.Descendants("Tag").First().Remove();
        if (kind == "default") after.Descendants("DefaultData").Single().Value = "999";
        var candidate = await Register(after); var request = new LogixValidationRequest("legacy-review-readback", Snapshot(candidate), Reference: Snapshot(source)); var result = await service.PreviewLogixValidationAsync(request);
        var finding = result.Findings.First(f => f.Check == check);
        Assert.Equal("fail", result.Status); await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(request with { ExpectedDifferences = [new(check, finding.Location, finding.ExpectedSha256, finding.ActualSha256, "Must remain blocked")] }));
    }
    [Fact]
    public async Task Export_target_markers_and_materialized_dependencies_are_metadata_not_code_or_layout()
    {
        var before = Document(); before.Descendants("Routine").First().SetAttributeValue("Use", "Target");
        before.Descendants("DataTypes").Single().Add(new XElement("DataType", new XAttribute("Name", "Example"), new XElement("Members", new XElement("Member", new XAttribute("Name", "Count"), new XAttribute("DataType", "DINT")))));
        var after = new XDocument(before); after.Root!.SetAttributeValue("SoftwareRevision", "32.04"); after.Descendants("Controller").Single().SetAttributeValue("MajorRev", "32"); after.Descendants("Module").Single().SetAttributeValue("Major", "32");
        after.Descendants("Routine").First().Attribute("Use")!.Remove(); after.Descendants("DataType").Single().Add(new XElement("Dependencies", new XElement("Dependency", new XAttribute("Name", "DINT"))));
        var source = await Register(before); var candidate = await Register(after); var result = await service.PreviewLogixValidationAsync(new("legacy-review-readback", Snapshot(candidate), Reference: Snapshot(source)));
        Assert.Contains(result.Checks, c => c.Name == "readable-routine-code" && c.Status == "pass"); Assert.Contains(result.Checks, c => c.Name == "data-type-layouts" && c.Status == "pass");
        Assert.Contains(result.Findings, f => f.Location.EndsWith("/@Use")); Assert.Contains(result.Findings, f => f.Check == "untouched-configuration" && f.Location.Contains("Dependencies"));
        after.Descendants("Member").Single().SetAttributeValue("DataType", "REAL"); candidate = await Register(after);
        Assert.Contains((await service.PreviewLogixValidationAsync(new("legacy-review-readback", Snapshot(candidate), Reference: Snapshot(source)))).Checks, c => c.Name == "data-type-layouts" && c.Status == "fail");
    }
    [Fact]
    public async Task Unsupported_protected_normalization_is_visible_and_never_drops_nested_payloads()
    {
        var before = Document(); before.Descendants("EncodedData").Single().Nodes().OfType<XText>().Single().Value = "not-a-base64-payload";
        var source = await Register(before); var after = Document(32); after.Descendants("EncodedData").Single().Nodes().OfType<XText>().Single().Value = "not-a-base64-payload"; var candidate = await Register(after);
        var result = await service.PreviewLogixValidationAsync(new("legacy-review-readback", Snapshot(candidate), Reference: Snapshot(source), NativeRules: new(NormalizeProtectedPayloadWhitespace: true)));
        Assert.Contains(result.Findings, f => f.Check == "protected-aoi-payload" && f.Status == "unsupported");
        before.Descendants("EncodedData").Single().Nodes().OfType<XText>().Single().Remove(); source = await Register(before);
        result = await service.PreviewLogixValidationAsync(new("legacy-review-readback", Snapshot(candidate), Reference: Snapshot(source)));
        Assert.Contains(result.Findings, f => f.Check == "protected-aoi-payload" && f.Status == "unsupported");
    }
    public void Dispose()
    {
        service.Dispose(); var absolute = Path.GetFullPath(root); var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RockwellLegacyReview")) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture cleanup path."); Directory.Delete(absolute, true);
    }
}
