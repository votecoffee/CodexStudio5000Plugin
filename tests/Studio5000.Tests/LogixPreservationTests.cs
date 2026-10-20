using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LogixPreservationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LogixPreservation", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public LogixPreservationTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private static XElement Scalar(int value) => new("Tag", new XAttribute("Name", "Scalar"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "DINT"),
        new XElement("Data", new XAttribute("Format", "L5K"), new XCData(value.ToString())), new XElement("Data", new XAttribute("Format", "Decorated"), new XElement("DataValue", new XAttribute("DataType", "DINT"), new XAttribute("Value", value))));
    private static XElement Routine(string name, string code = "NOP();") => new("Routine", new XAttribute("Name", name), new XAttribute("Type", "RLL"),
        new XElement("Description", new XCData("Routine annotation")), new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "0"), new XAttribute("Type", "N"), new XElement("Comment", new XCData("Rung annotation")), new XElement("Text", new XCData(code)))));
    private static XDocument Document(int value = 1, string code = "NOP();", string remote = "Original")
    {
        var message = new XElement("Tag", new XAttribute("Name", "Msg"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "MESSAGE"), new XElement("Description", new XCData("Fresh documentation")),
            new XElement("Data", new XAttribute("Format", "Message"), new XElement("MessageParameters", new XAttribute("RemoteElement", remote), new XAttribute("ConnectionPath", "1, 0"), new XAttribute("RequestLength", "1"), new XAttribute("CacheConnections", "false"))));
        var program = new XElement("Program", new XAttribute("Name", "DemoProgram"), new XAttribute("MainRoutineName", "Main"), new XElement("Tags"),
            new XElement("Routines", Routine("Main", code), new XElement("Routine", new XAttribute("Name", "Empty"), new XAttribute("Type", "RLL"))));
        var controller = new XElement("Controller", new XAttribute("Name", "Demo"), new XAttribute("MajorRev", "35"), new XElement("DataTypes"), new XElement("AddOnInstructionDefinitions"),
            new XElement("Modules", new XElement("Module", new XAttribute("Name", "Local"), new XAttribute("Inhibit", "false"))), new XElement("Tasks", new XElement("Task", new XAttribute("Name", "MainTask"), new XAttribute("Rate", "10"))),
            new XElement("Tags", Scalar(value), new XElement("Tag", new XAttribute("Name", "Alias"), new XAttribute("TagType", "Alias"), new XAttribute("AliasFor", "Scalar")), message), new XElement("Programs", program));
        return new(new XDeclaration("1.0", "utf-8", null), new XElement("RSLogix5000Content", new XAttribute("SoftwareRevision", "35.00"), controller));
    }
    private static XElement Tag(XDocument doc, string name) => doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == name);
    private static XElement Message(XDocument doc) => Tag(doc, "Msg").Element("Data")!.Element("MessageParameters")!;
    private static XElement Program(XDocument doc) => doc.Descendants("Program").Single();
    private async Task<ProjectInfo> Register(XDocument doc, bool bom = false)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".L5X");
        var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + doc.ToString(SaveOptions.DisableFormatting).Replace("\n", "\r\n");
        File.WriteAllText(path, text, new UTF8Encoding(bom)); return await service.RegisterAsync(path);
    }
    private XDocument Revision(ProjectInfo p) => XDocument.Load(Path.Combine(root, "state", "projects", p.Id, "revision-" + p.Revision + ".l5x"), LoadOptions.PreserveWhitespace);
    private static MergeSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.CurrentSha256);
    private static LogicValueMergeRequest Policy(ProjectInfo a, ProjectInfo b, ProjectInfo c, params MessageFieldChoice[] choices) => new(Snapshot(a), Snapshot(b), Snapshot(c), Mode: "preserve-fresh", ConfigurationFields: choices);
    private async Task<(ProjectInfo A, ProjectInfo B, ProjectInfo C)> Inputs(string code = "XIC(Scalar)NOP();") => (await Register(Document()), await Register(Document(code: code, remote: "Reviewed")), await Register(Document(41, remote: "Current"), true));

    [Fact]
    public async Task Preserves_both_saved_representations_and_only_explicit_MESSAGE_fields()
    {
        var (a, b, _) = await Inputs("CPS(\"literal </Routine> >\",Scalar,1);");
        var fresh = Document(41, remote: "Current"); Message(fresh).SetAttributeValue("RequestLength", "9"); fresh.Descendants("Module").Single().SetAttributeValue("Inhibit", "true");
        var c = await Register(fresh, true); var request = Policy(a, b, c, new MessageFieldChoice("msg", "RemoteElement", "logic"));
        var preview = await service.PreviewLogicValueMergeAsync(request); Assert.True(preview.CanApply); Assert.Equal(1, preview.Preservation!.MessageFieldsApplied);
        var output = await service.ApplyLogicValueMergeAsync(new(request, preview.ReviewSha256)); var doc = Revision(output);
        Assert.Equal("Reviewed", (string?)Message(doc).Attribute("RemoteElement")); Assert.Equal("9", (string?)Message(doc).Attribute("RequestLength"));
        Assert.Equal("41", Tag(doc, "Scalar").Elements("Data").Single(d => (string?)d.Attribute("Format") == "L5K").Value);
        Assert.Equal("41", (string?)Tag(doc, "Scalar").Descendants("DataValue").Single().Attribute("Value")); Assert.Equal("true", (string?)doc.Descendants("Module").Single().Attribute("Inhibit"));
        var bytes = File.ReadAllBytes(Path.Combine(root, "state", "projects", output.Id, "revision-0.l5x")); Assert.True(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 })); Assert.Contains("\r\n", Encoding.UTF8.GetString(bytes));
        Assert.Contains("literal </Routine>", doc.Descendants("Text").Single().Value);
        Assert.All(new[] { a, b, c }, p => Assert.Equal(p.SourceSha256, ProjectService.Hash(File.ReadAllBytes(p.SourcePath))));
        var validation = await service.PreviewLogixValidationAsync(new("merge-preservation", Snapshot(output), Merge: request, ExpectedPolicySha256: preview.PolicySha256));
        Assert.Equal("pass", validation.Status); Assert.Empty(validation.Findings); Assert.Equal(WorkspaceIdentity.Version, validation.ToolVersion);
    }

    [Fact]
    public async Task MESSAGE_conflicts_name_exact_fields_and_a_new_choice_invalidates_the_review()
    {
        var (a, b, c) = await Inputs(); var request = Policy(a, b, c); var blocked = await service.PreviewLogicValueMergeAsync(request);
        Assert.False(blocked.CanApply); Assert.Contains(blocked.Findings!, f => f.Kind == "message-field" && f.Location.EndsWith("/@RemoteElement") && f.BaselineValue == "Original" && f.LogicValue == "Reviewed" && f.FreshValue == "Current");
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyLogicValueMergeAsync(new(request, blocked.ReviewSha256)));
        var resolved = request with { ConfigurationFields = [new("Msg", "RemoteElement", "fresh")] }; var review = await service.PreviewLogicValueMergeAsync(resolved); Assert.True(review.CanApply);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyLogicValueMergeAsync(new(resolved, blocked.ReviewSha256)));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(new("merge-preservation", Snapshot(c), Merge: resolved, ExpectedPolicySha256: blocked.PolicySha256)));
    }

    [Fact]
    public async Task MESSAGE_lexical_attribute_additions_escape_values_without_rewriting_other_fields()
    {
        var baseline = Document(); Message(baseline).Attribute("ConnectionPath")!.Remove(); var logic = new XDocument(baseline);
        Message(logic).SetAttributeValue("ConnectionPath", "Port<&\"' >"); var a = await Register(baseline); var b = await Register(logic); var c = await Register(baseline);
        var request = Policy(a, b, c, new MessageFieldChoice("Msg", "ConnectionPath", "logic")); var preview = await service.PreviewLogicValueMergeAsync(request);
        Assert.True(preview.CanApply); var merged = await service.ApplyLogicValueMergeAsync(new(request, preview.ReviewSha256)); Assert.Equal("Port<&\"' >", (string?)Message(Revision(merged)).Attribute("ConnectionPath"));
        Assert.Equal("1", (string?)Message(Revision(merged)).Attribute("RequestLength"));
    }

    [Fact]
    public async Task Unsupported_encoding_and_unreviewed_nonroutine_configuration_are_not_silently_transferred()
    {
        var a = await Register(Document()); var changed = Document(code: "XIC(Scalar)NOP();"); changed.Descendants("Task").Single().SetAttributeValue("Rate", "99"); var b = await Register(changed); var c = await Register(Document());
        var preview = await service.PreviewLogicValueMergeAsync(Policy(a, b, c)); Assert.False(preview.CanApply); Assert.Contains(preview.Findings!, f => f.Kind == "configuration" && f.Status == "unsupported");
        var path = Path.Combine(root, "utf16.L5X"); File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-16\"?>" + Document().ToString(SaveOptions.DisableFormatting), Encoding.Unicode); var utf16 = await service.RegisterAsync(path);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogicValueMergeAsync(Policy(a, a, utf16))); Assert.Equal("merge_encoding", error.Code);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("type")]
    [InlineData("safety")]
    [InlineData("fresh-logic")]
    public async Task Incompatible_contracts_and_independent_fresh_logic_block_apply(string change)
    {
        var (a, b, _) = await Inputs(); var fresh = Document(41, remote: "Current");
        if (change == "alias") Tag(fresh, "Alias").SetAttributeValue("AliasFor", "Unknown");
        if (change == "type") Tag(fresh, "Scalar").SetAttributeValue("DataType", "REAL");
        if (change == "safety") Program(fresh).SetAttributeValue("Class", "Safety");
        if (change == "fresh-logic") fresh.Descendants("Text").Single().Value = "XIO(Scalar)NOP();";
        var c = await Register(fresh); var request = Policy(a, b, c, new MessageFieldChoice("Msg", "RemoteElement", "logic")); var preview = await service.PreviewLogicValueMergeAsync(request);
        Assert.False(preview.CanApply); await Assert.ThrowsAsync<OperationException>(() => service.ApplyLogicValueMergeAsync(new(request, preview.ReviewSha256)));
    }

    [Fact]
    public async Task Whole_project_profile_handles_thousands_of_tags_and_over_100000_elements()
    {
        var baseline = Document(); var tags = baseline.Root!.Element("Controller")!.Element("Tags")!;
        for (var i = 0; i < 3032; i++) { var tag = Scalar(i); tag.SetAttributeValue("Name", "Extra" + i); tags.Add(tag); }
        tags.Add(new XElement("Tag", new XAttribute("Name", "LargeArray"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "DINT"), new XAttribute("Dimensions", "110000"),
            new XElement("Data", new XAttribute("Format", "Decorated"), new XElement("Array", new XAttribute("DataType", "DINT"), new XAttribute("Dimensions", "110000"), Enumerable.Range(0, 110000).Select(i => new XElement("Element", new XAttribute("Index", "[" + i + "]"), new XAttribute("Value", i)))))));
        var logic = new XDocument(baseline); logic.Descendants("Text").Single().Value = "XIC(Scalar)NOP();"; var fresh = new XDocument(baseline); Tag(fresh, "Scalar").Elements("Data").Single(d => (string?)d.Attribute("Format") == "L5K").Value = "41";
        Tag(fresh, "Scalar").Descendants("DataValue").Single().SetAttributeValue("Value", "41");
        var a = await Register(baseline); var b = await Register(logic); var c = await Register(fresh); var preview = await service.PreviewLogicValueMergeAsync(Policy(a, b, c));
        Assert.True(preview.CanApply); Assert.Equal(3036, preview.Preservation!.TagRecords); Assert.Equal(3034, preview.Preservation.PreservedRepresentations["Decorated"]);
        var legacy = await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogicValueMergeAsync(new(Snapshot(a), Snapshot(b), Snapshot(c)))); Assert.Equal("comparison_limit", legacy.Code);
    }

    [Fact]
    public async Task Additions_and_removals_preserve_annotations_and_empty_container_metadata()
    {
        var before = Document(); var reviewed = new XDocument(before); var routines = Program(reviewed).Element("Routines")!;
        routines.Elements("Routine").Single(r => (string?)r.Attribute("Name") == "Empty").Remove(); routines.Add(Routine("Added", "NOP();"));
        var a = await Register(before); var b = await Register(reviewed); var c = await Register(before); var request = Policy(a, b, c); var preview = await service.PreviewLogicValueMergeAsync(request);
        Assert.Equal(1, preview.Preservation!.AddedRoutines); Assert.Equal(1, preview.Preservation.RemovedRoutines);
        var merged = await service.ApplyLogicValueMergeAsync(new(request, preview.ReviewSha256)); Assert.Equal(new[] { "Main", "Added" }, Revision(merged).Descendants("Routine").Select(r => (string?)r.Attribute("Name")));
        Assert.Equal("Rung annotation", Revision(merged).Descendants("Routine").Last().Descendants("Comment").Single().Value);
        var empty = Document(); Program(empty).Attribute("MainRoutineName")!.Remove(); Program(empty).Element("Routines")!.ReplaceWith(new XElement("Routines", new XAttribute("VendorMetadata", "retain")));
        var add = new XDocument(empty); Program(add).Element("Routines")!.Add(Routine("Added")); a = await Register(empty); b = await Register(add); c = await Register(empty);
        request = Policy(a, b, c); preview = await service.PreviewLogicValueMergeAsync(request); merged = await service.ApplyLogicValueMergeAsync(new(request, preview.ReviewSha256)); Assert.Equal("retain", (string?)Revision(merged).Descendants("Routines").Single().Attribute("VendorMetadata"));
    }

    [Fact]
    public async Task Validation_detects_single_representation_drift_and_unapproved_configuration()
    {
        var (a, b, c) = await Inputs(); var request = Policy(a, b, c, new MessageFieldChoice("Msg", "RemoteElement", "logic")); var preview = await service.PreviewLogicValueMergeAsync(request);
        var merged = await service.ApplyLogicValueMergeAsync(new(request, preview.ReviewSha256)); var damaged = Revision(merged);
        Tag(damaged, "Scalar").Elements("Data").Single(d => (string?)d.Attribute("Format") == "L5K").Value = "999"; Message(damaged).SetAttributeValue("RequestLength", "99"); damaged.Descendants("Task").Single().SetAttributeValue("Rate", "20");
        Tag(damaged, "Scalar").AddFirst(new XComment("Unapproved XML annotation"));
        var bad = await Register(damaged); var report = await service.PreviewLogixValidationAsync(new("merge-preservation", Snapshot(bad), Merge: request, ExpectedPolicySha256: preview.PolicySha256));
        Assert.Equal("fail", report.Status); Assert.Contains(report.Findings, f => f.Check == "stored-value-representations" && f.Location.Contains("L5K"));
        Assert.Contains(report.Findings, f => f.Location.EndsWith("@RequestLength")); Assert.Contains(report.Findings, f => f.Check == "untouched-configuration" && f.Location.EndsWith("@Rate"));
        Assert.Contains(report.Findings, f => f.Check == "tag-identities-definitions-aliases");
        Assert.Contains(report.UnsupportedCoverage, s => s.Contains("Safety semantics"));
    }

    [Fact]
    public async Task Native_readback_normalizations_are_opt_in_and_preserve_quoted_spaces()
    {
        var intended = Document(); var readback = new XDocument(intended); Tag(readback, "Scalar").Elements("Data").First().Value = " 1 \r\n";
        Message(readback).SetAttributeValue("ConnectionPath", "1,0"); Message(readback).SetAttributeValue("CacheConnections", "FALSE"); readback.Descendants("Text").Single().Value = "NOP( );";
        var reference = await Register(intended); var candidate = await Register(readback);
        Assert.Equal("fail", (await service.PreviewLogixValidationAsync(new("native-readback", Snapshot(candidate), Reference: Snapshot(reference)))).Status);
        var rules = new NativeReadbackRules(true, true, NormalizeRllWhitespace: true);
        Assert.Equal("pass", (await service.PreviewLogixValidationAsync(new("native-readback", Snapshot(candidate), Reference: Snapshot(reference), NativeRules: rules))).Status);
        Tag(intended, "Scalar").Elements("Data").First().Value = "\"A B\""; Tag(readback, "Scalar").Elements("Data").First().Value = "\"AB\"";
        reference = await Register(intended); candidate = await Register(readback);
        Assert.Equal("fail", (await service.PreviewLogixValidationAsync(new("native-readback", Snapshot(candidate), Reference: Snapshot(reference), NativeRules: rules))).Status);
    }

    [Fact]
    public async Task Native_expected_change_policy_binds_exact_fields_and_hashes_without_hiding_findings()
    {
        var expected = await Register(Document()); var native = Document(); native.Descendants("Task").Single().SetAttributeValue("Rate", "20"); var candidate = await Register(native);
        var request = new LogixValidationRequest("native-readback", Snapshot(candidate), Reference: Snapshot(expected)); var report = await service.PreviewLogixValidationAsync(request);
        Assert.Equal("fail", report.Status); var difference = Assert.Single(report.Findings);
        var approval = new LogixExpectedDifference(difference.Check, difference.Location, difference.ExpectedSha256, difference.ActualSha256, "Reviewed target task period for this exact readback");
        var reviewed = await service.PreviewLogixValidationAsync(request with { ExpectedDifferences = [approval] });
        Assert.Equal("pass", reviewed.Status); Assert.Single(reviewed.Findings, f => f.Classification == "expected"); Assert.Equal(1, reviewed.Checks.Single(c => c.Name == "untouched-configuration").Expected);
        Assert.NotEqual(report.PolicySha256, reviewed.PolicySha256); Assert.NotEqual(report.ReviewSha256, reviewed.ReviewSha256);
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(request with { ExpectedDifferences = [approval with { ActualSha256 = new string('0', 64) }] }));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(request with { ExpectedDifferences = [approval, approval] }));
    }

    [Fact]
    public async Task Integrity_distinguishes_inherited_new_and_resolved_findings_and_retains_hash_bound_receipts()
    {
        var baseline = await Register(Document(code: "JSR(Missing,0);")); var same = await Register(Document(code: "JSR(Missing,0);"));
        var inherited = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(same), Baseline: Snapshot(baseline)));
        Assert.Equal("pass", inherited.Status); Assert.Single(inherited.Findings, f => f.Classification == "inherited");
        var changed = await Register(Document(code: "JSR(NewMissing,0);")); var request = new LogixValidationRequest("offline-integrity", Snapshot(changed), Baseline: Snapshot(baseline)); var report = await service.PreviewLogixValidationAsync(request);
        Assert.Equal("fail", report.Status); Assert.Contains(report.Findings, f => f.Classification == "new"); Assert.Contains(report.Findings, f => f.Classification == "resolved");
        await Assert.ThrowsAsync<OperationException>(() => service.RecordLogixValidationAsync(new(request, inherited.ReviewSha256)));
        var record = await service.RecordLogixValidationAsync(new(request, report.ReviewSha256)); var retained = service.LogixValidationReceipt(record.Artifact.Id, record.Artifact.Sha256);
        Assert.Equal(report.ReviewSha256, retained.Report.ReviewSha256); Assert.Equal("fail", retained.Report.Status); Assert.Equal(0, service.Get(changed.Id).Revision); Assert.Empty(service.SdkJobList());
        Assert.Throws<OperationException>(() => service.LogixValidationReceipt(record.Artifact.Id, new string('0', 64)));
        File.AppendAllText(service.Download(record.Artifact.Id).Path, " "); Assert.Throws<OperationException>(() => service.LogixValidationReceipt(record.Artifact.Id, record.Artifact.Sha256));
    }
    public void Dispose()
    {
        service.Dispose();
        var parent = Path.GetDirectoryName(Path.GetFullPath(root));
        if (!string.Equals(parent, Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LogixPreservation")), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cleanup escaped the owned test directory.");
        Directory.Delete(root, true);
    }
}
