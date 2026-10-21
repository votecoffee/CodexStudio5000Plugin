using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ControllerTagRenameTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellTagRename", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public ControllerTagRenameTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private static string Source(string code = "XIC(Old)ADD(Old,1,Old);", string locals = "", string attributes = "", string extra = "") =>
        "<?xml version='1.0' encoding='utf-8'?>\r\n<!-- retained Old -->\n<RSLogix5000Content><Controller Name='Demo' " + attributes + "><Tags>" +
        "<Tag Name = 'Old' TagType='Base' DataType='DINT'><Description><![CDATA[Old documentation]]></Description><Data Format='L5K'><![CDATA[41\r\n]]></Data><Data Format='Decorated'><DataValue DataType='DINT' Value='41'/></Data><Data Format='Opaque'><![CDATA[Old untouched payload]]></Data><ForceData><![CDATA[0]]></ForceData></Tag>" +
        "<Tag Name='Alias' TagType='Alias' AliasFor='Old'/></Tags><Programs><Program Name='DemoProgram'><Tags>" + locals + "</Tags><Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Comment><![CDATA[Old comment]]></Comment><Text><![CDATA[" + code + "]]></Text></Rung></RLLContent></Routine></Routines></Program></Programs>" + extra + "</Controller></RSLogix5000Content>\r\n";
    private async Task<ProjectInfo> Register(string text, Encoding? encoding = null)
    { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); File.WriteAllText(path, text, encoding ?? new UTF8Encoding(true)); return await service.RegisterAsync(path); }
    private byte[] Revision(ProjectInfo p) => File.ReadAllBytes(Path.Combine(root, "state", "projects", p.Id, "revision-" + p.Revision + ".l5x"));
    private static ControllerTagRenameRequest Request(ProjectInfo p) => new(p.Id, p.CurrentSha256, "Old", "Renamed");

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Renames_exact_identifiers_preserving_every_other_byte_and_representation(bool unicode)
    {
        var text = Source("XIC(Old.Bit)ADD(Old[Old],1,Old);"); var encoding = unicode ? (Encoding)new UnicodeEncoding(false, true, true) : new UTF8Encoding(true, true);
        text = text.Replace("encoding='utf-8'", "encoding='" + encoding.WebName + "'"); var p = await Register(text, encoding); var before = Revision(p); var request = Request(p);
        var plan = await service.PreviewControllerTagRenameAsync(request); Assert.True(plan.CanApply); Assert.False(plan.ReviewOnly); Assert.Equal(3, plan.PreservedRepresentations.Count);
        var result = await service.ApplyControllerTagRenameAsync(new(request, plan.ReviewSha256));
        var expected = text.Replace("Name = 'Old'", "Name = 'Renamed'").Replace("AliasFor='Old'", "AliasFor='Renamed'").Replace("XIC(Old.Bit)ADD(Old[Old],1,Old);", "XIC(Renamed.Bit)ADD(Renamed[Renamed],1,Renamed);");
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)), Revision(result.Project)); Assert.Equal(before, Revision(p)); Assert.Equal(before, File.ReadAllBytes(p.SourcePath));
        Assert.Equal(plan.ResultSha256, result.Project.CurrentSha256); Assert.NotEqual(p.Id, result.Project.Id);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(service.Download(result.Receipt.Id).Path)); Assert.Equal(plan.ReviewSha256, receipt.RootElement.GetProperty("plan").GetProperty("reviewSha256").GetString());
    }
    [Fact]
    public async Task Program_scope_shadowing_and_explicit_addresses_are_preserved_and_new_capture_blocks()
    {
        var local = "<Tag Name='Old' TagType='Base' DataType='DINT'/><Tag Name='LocalAlias' TagType='Alias' AliasFor='Old'/>";
        var p = await Register(Source("XIC(Old)MOV(Program:DemoProgram.Old,Old);", local)); var request = Request(p); var plan = await service.PreviewControllerTagRenameAsync(request);
        Assert.True(plan.CanApply); Assert.Equal(3, plan.References.Count(r => r.Action == "program-local-preserved")); Assert.Contains(plan.References, r => r.Action == "program-qualified-preserved");
        var output = await service.ApplyControllerTagRenameAsync(new(request, plan.ReviewSha256)); var doc = XmlProjects.Parse(Revision(output.Project)); Assert.Equal("XIC(Old)MOV(Program:DemoProgram.Old,Old);", doc.Descendants("Text").Single().Value);
        var capture = await Register(Source(locals: "<Tag Name='Renamed' TagType='Base' DataType='DINT'/>")); var blocked = await service.PreviewControllerTagRenameAsync(Request(capture));
        Assert.False(blocked.CanApply); Assert.Contains(blocked.References, r => r.Action == "blocked-capture"); await Assert.ThrowsAsync<OperationException>(() => service.ApplyControllerTagRenameAsync(new(Request(capture), blocked.ReviewSha256)));
    }
    [Fact]
    public async Task Ambiguous_operands_require_exact_reasoned_decisions_while_routine_symbols_and_literals_stay()
    {
        var code = "JSR(Old,0,Old)LBL(Old)JMP(Old)GSV(Old,Old,Old,Old)Custom(Old)CPS('Old',Old,1);";
        var p = await Register(Source(code)); var request = Request(p); var first = await service.PreviewControllerTagRenameAsync(request); Assert.False(first.CanApply);
        Assert.Equal(4, first.References.Count(r => r.Action == "decision-required")); Assert.Equal(3, first.References.Count(r => r.Action == "symbol-preserved"));
        var decisions = first.References.Where(r => r.Action == "decision-required").Select(r => new TagRenameDecision(r.Id, r.Kind == "RLL" && r.Start == code.IndexOf("Custom(Old)") + 7 ? "rename" : "leave-symbol", "Reviewed generated instruction operands")).ToArray();
        request = request with { Decisions = decisions }; var plan = await service.PreviewControllerTagRenameAsync(request); Assert.True(plan.CanApply);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyControllerTagRenameAsync(new(request, first.ReviewSha256)));
        var output = await service.ApplyControllerTagRenameAsync(new(request, plan.ReviewSha256)); Assert.Equal("JSR(Old,0,Renamed)LBL(Old)JMP(Old)GSV(Old,Old,Old,Renamed)Custom(Renamed)CPS('Old',Renamed,1);", XmlProjects.Parse(Revision(output.Project)).Descendants("Text").Single().Value);
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewControllerTagRenameAsync(request with { Decisions = [new("stale", "rename", "Old decision")] }));
    }
    [Fact]
    public async Task ST_multiline_comments_entities_and_XML_offsets_keep_original_lexical_bytes()
    {
        var text = Source().Replace("Type='RLL'><RLLContent><Rung Number='0'><Comment><![CDATA[Old comment]]></Comment><Text><![CDATA[XIC(Old)ADD(Old,1,Old);]]></Text></Rung></RLLContent>",
            "Type='ST'><STContent><Line Number='0'>/* Old\r\n</Line><Line Number='1'>Old */ &#79;ld := Old + 1; // Old</Line><Line Number='2'><![CDATA[IF Old > 0 THEN\r\n Old := Old + 1; END_IF;]]></Line></STContent>");
        var p = await Register(text); var request = Request(p); var plan = await service.PreviewControllerTagRenameAsync(request); Assert.True(plan.CanApply);
        var output = await service.ApplyControllerTagRenameAsync(new(request, plan.ReviewSha256));
        var expected = text.Replace("Name = 'Old'", "Name = 'Renamed'").Replace("AliasFor='Old'", "AliasFor='Renamed'").Replace("&#79;ld := Old + 1", "Renamed := Renamed + 1").Replace("IF Old > 0 THEN\r\n Old := Old + 1", "IF Renamed > 0 THEN\r\n Renamed := Renamed + 1");
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(expected)), Revision(output.Project));
    }
    [Theory] [InlineData("XIC(Ol<![CDATA[d]]>)OTE(Old);")] [InlineData("XIC(Ol<!--split-->d)OTE(Old);")]
    public async Task Identifiers_crossing_XML_segments_block_instead_of_partial_rewriting(string code)
    {
        var text = Source().Replace("<![CDATA[XIC(Old)ADD(Old,1,Old);]]>", code); var p = await Register(text); var plan = await service.PreviewControllerTagRenameAsync(Request(p)); Assert.False(plan.CanApply); Assert.Contains(plan.References, r => r.Action == "blocked-source");
    }
    [Theory] [InlineData("declaration")] [InlineData("program")] [InlineData("mapping")]
    public async Task Safety_changes_default_to_block_and_require_explicit_review_only_handling(string scope)
    {
        var text = Source(attributes: scope == "mapping" ? "SafetyTagMap='Old=SafeTarget' SafetySignature='retain'" : "SafetySignature='retain'");
        if (scope == "declaration") text = text.Replace("Name = 'Old'", "Name = 'Old' Class='Safety'"); if (scope == "program") text = text.Replace("Name='DemoProgram'", "Name='DemoProgram' Class='Safety'");
        var p = await Register(text); var request = Request(p); var blocked = await service.PreviewControllerTagRenameAsync(request); Assert.False(blocked.CanApply); Assert.True(blocked.ReviewOnly);
        request = request with { SafetyHandling = "review-only" }; var plan = await service.PreviewControllerTagRenameAsync(request); Assert.True(plan.CanApply); Assert.True(plan.ReviewOnly);
        var result = await service.ApplyControllerTagRenameAsync(new(request, plan.ReviewSha256)); Assert.True(result.ReviewOnly); Assert.StartsWith("REVIEW ONLY", result.Project.Name); Assert.Contains("SafetySignature='retain'", Encoding.UTF8.GetString(Revision(result.Project)));
        if (scope == "mapping") Assert.Contains("SafetyTagMap='Renamed=SafeTarget'", Encoding.UTF8.GetString(Revision(result.Project)));
    }
    [Fact]
    public async Task Unsupported_visible_references_stale_hashes_and_destination_collisions_are_rejected()
    {
        var p = await Register(Source(extra: "<Other Input='Old'/>")); var plan = await service.PreviewControllerTagRenameAsync(Request(p)); Assert.False(plan.CanApply);
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewControllerTagRenameAsync(Request(p) with { ExpectedSha256 = new string('0', 64) }));
        foreach (var name in new[] { "Alias", "TRUE", "AND", "IF", "Invalid.Name", new string('a', 41) }) await Assert.ThrowsAsync<OperationException>(() => service.PreviewControllerTagRenameAsync(Request(p) with { NewName = name }));
        var plain = await Register(Source()); var casing = Request(plain) with { NewName = "old" }; Assert.True((await service.PreviewControllerTagRenameAsync(casing)).CanApply);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
