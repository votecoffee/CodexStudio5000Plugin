using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class StructuralEditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellStructural", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private readonly SdkInputFile plan;
    private const string Wire = "<Tag Name='Wire' TagType='Consumed' DataType='Proto'><ConsumeInfo Producer='Peer' RemoteTag='WireOut'/><Data Format='L5K'><![CDATA[[1]]></Data><Data Format='Decorated'><Structure DataType='Proto'><DataValueMember Name='Gate' DataType='BOOL' Value='1'/></Structure></Data></Tag>";
    private const string Type = "<DataType Name='Proto'><Members><Member Name='Gate' DataType='BOOL'/></Members></DataType>";
    private const string Old = "<Routine Name='Old' Type='RLL'><RLLContent><Rung Number='0'><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine>";
    private const string MainText = "/RSLogix5000Content/Controller/Programs/Program[@Name='App']/Routines/Routine[@Name='Main']/RLLContent/Rung/Text";
    private static string Source() => "<?xml version='1.0' encoding='utf-8'?>\r\n<!-- μ 🚀 preserved -->\n<RSLogix5000Content><Controller Name='Demo'><SafetyInfo Signature='retained'/><DataTypes>" + Type + "</DataTypes><AddOnInstructionDefinitions><EncodedData Name='NativeLibrary' SignatureID='kept'><![CDATA[opaque </Routine> payload\r\n]]></EncodedData></AddOnInstructionDefinitions><Tags>" + Wire +
        "<Tag Name = 'Keep' TagType='Base' DataType='DINT'><Description><![CDATA[fresh]]></Description><Data Format='L5K'><![CDATA[41\r\n]]></Data><Data Format='Decorated'><DataValue DataType='DINT' Value='41'/></Data></Tag></Tags><Programs><Program Name='App' MainRoutineName='Main'><Tags /><Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Text><![CDATA[JSR(Old,0);MOV(Keep,Keep);]]></Text></Rung></RLLContent></Routine>" + Old +
        "<Routine Name='Library' Type='RLL' SignatureID='retain'><RLLContent><Rung Number='0'><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>\r\n";
    public StructuralEditTests()
    {
        Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] });
        var path = Path.Combine(root, "review.json"); File.WriteAllText(path, "{\"purpose\":\"generated offline review fixture\"}"); plan = new(path, ProjectService.Hash(File.ReadAllBytes(path)));
    }
    private async Task<ProjectInfo> Register(string? text = null, Encoding? encoding = null)
    { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); File.WriteAllText(path, text ?? Source(), encoding ?? new UTF8Encoding(true)); return await service.RegisterAsync(path); }
    private async Task<StructuralEdit> Edit(ProjectInfo p, string operation, string xpath, string? value = null)
    { var context = await service.StructuralContextAsync(new(p.Id, p.CurrentSha256, p.Revision, xpath)); return new(operation, xpath, context.Targets.Single().ElementSha256, value); }
    private StructuralEditRequest Request(ProjectInfo p, params StructuralEdit[] edits)
    {
        var dispositions = new List<StructuralValueDisposition>();
        foreach (var edit in edits)
        {
            if (edit.XPath == Tag("Wire") && edit.Operation is "remove_element" or "replace_element") dispositions.Add(new(new("Wire"), edit.Operation == "remove_element" ? "remove" : "replace-reviewed", "Generated fixture: reviewed saved protocol state disposition."));
            if (edit.Operation == "add_element" && XElement.Parse(edit.Value!).Name == "Tag") dispositions.Add(new(new((string)XElement.Parse(edit.Value!).Attribute("Name")!, "App"), "initialize-reviewed", "Generated fixture: explicit new tag initialization."));
        }
        return new(p.Id, p.CurrentSha256, p.Revision, plan,
            new([new("App", "Main"), new("App", "Old"), new("App", "New"), new("App", "Library")], [new("Wire"), new("Keep"), new("Alias"), new("A", "App"), new("B", "App")], ["Proto", "Replacement"], AcknowledgeIncompleteBindings: true, ValueDispositions: dispositions.ToArray()), edits);
    }
    private static StructuralApplyRequest Apply(StructuralEditPreview preview) => new(preview.Request, preview.ReviewSha256, preview.PreviewReceipt!.Id, preview.PreviewReceipt.Sha256);
    private static string Tag(string name) => "/RSLogix5000Content/Controller/Tags/Tag[@Name='" + name + "']";
    private static string Routine(string name) => "/RSLogix5000Content/Controller/Programs/Program[@Name='App']/Routines/Routine[@Name='" + name + "']";
    private byte[] Revision(ProjectInfo p) => File.ReadAllBytes(Path.Combine(root, "state", "projects", p.Id, "revision-" + p.Revision + ".l5x"));

    [Theory] [InlineData("utf-8")] [InlineData("utf-16")] [InlineData("utf-32")]
    public async Task Coherent_batch_preserves_all_untouched_tag_library_and_source_bytes(string name)
    {
        var encoding = Encoding.GetEncoding(name); var text = Source().Replace("encoding='utf-8'", "encoding='" + name + "'"); var p = await Register(text, encoding);
        var request = Request(p, await Edit(p, "remove_element", Tag("Wire")), await Edit(p, "remove_element", "/RSLogix5000Content/Controller/DataTypes/DataType[@Name='Proto']"), await Edit(p, "remove_element", Routine("Old")), await Edit(p, "set_text", MainText, "MOV(Keep,Keep);"));
        var preview = await service.PreviewStructuralEditsAsync(request); Assert.True(preview.CanApply); Assert.Equal("structural-byte-spans-v1", preview.XmlPreservation); Assert.Equal(1, preview.Preservation.UntouchedTags); Assert.True(preview.Preservation.UnselectedBytesPreserved);
        var beforeStatus = await service.StructuralEditStatusAsync(preview.PreviewReceipt!.Id, preview.PreviewReceipt.Sha256); Assert.Equal("previewed", beforeStatus.Status);
        var result = await service.ApplyStructuralEditsAsync(Apply(preview)); var expected = text.Replace(Wire, "").Replace(Type, "").Replace(Old, "").Replace("JSR(Old,0);MOV(Keep,Keep);", "MOV(Keep,Keep);");
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)), Revision(result.Project)); Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(text)), Revision(p)); Assert.Equal(Revision(p), File.ReadAllBytes(p.SourcePath));
        Assert.Equal(result.Project.CurrentSha256, result.Candidate.Sha256); Assert.Equal(Revision(result.Project), File.ReadAllBytes(service.Download(result.Candidate.Id).Path)); Assert.Equal("not-performed", result.NativeAcceptance); Assert.True(result.ReviewOnly);
        var status = await service.StructuralEditStatusAsync(preview.PreviewReceipt.Id, preview.PreviewReceipt.Sha256); Assert.Equal("applied", status.Status); Assert.Equal(result.Project.Id, status.Result!.Project.Id);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(service.Download(result.Receipt.Id).Path)); Assert.Equal(preview.ReviewSha256, receipt.RootElement.GetProperty("preview").GetProperty("reviewSha256").GetString());
    }
    [Fact]
    public async Task Adds_to_self_closing_containers_and_replacements_keep_every_other_byte()
    {
        var p = await Register(); var parent = "/RSLogix5000Content/Controller/Programs/Program[@Name='App']/Tags";
        const string a = "<Tag Name='A' TagType='Base' DataType='DINT'><Data Format='L5K'>7</Data></Tag>"; const string b = "<Tag Name='B' TagType='Base' DataType='BOOL'/>";
        var request = Request(p, await Edit(p, "add_element", parent, a), await Edit(p, "add_element", parent, b), await Edit(p, "set_text", MainText, "MOV(Keep,Keep);literal ]]> μ"));
        var preview = await service.PreviewStructuralEditsAsync(request); Assert.True(preview.CanApply); var result = await service.ApplyStructuralEditsAsync(Apply(preview));
        var expected = Source().Replace("<Tags />", "<Tags >" + a + b + "</Tags>").Replace("JSR(Old,0);MOV(Keep,Keep);", "MOV(Keep,Keep);literal ]]]]><![CDATA[> μ");
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(expected)), Revision(result.Project));
    }
    [Fact]
    public async Task Layout_changes_require_explicit_owned_tag_representations_and_protect_library_dependencies()
    {
        var p = await Register(); var type = await Edit(p, "replace_element", "/RSLogix5000Content/Controller/DataTypes/DataType", Type.Replace("</Members>", "<Member Name='Count' DataType='DINT'/></Members>"));
        var blocked = await service.PreviewStructuralEditsAsync(Request(p, type)); Assert.False(blocked.CanApply); Assert.Contains(blocked.Findings, f => f.Check == "changed-layout-values");
        var newWire = Wire.Replace("<![CDATA[[1]]]>", "<![CDATA[[1,42]]]>").Replace("</Structure>", "<DataValueMember Name='Count' DataType='DINT' Value='42'/></Structure>");
        var allowed = await service.PreviewStructuralEditsAsync(Request(p, type, await Edit(p, "replace_element", Tag("Wire"), newWire))); Assert.True(allowed.CanApply);
        var protectedSource = await Register(Source().Replace("<EncodedData Name='NativeLibrary'", "<EncodedData DataType='Proto' Name='NativeLibrary'"));
        var protectedType = await Edit(protectedSource, "replace_element", "/RSLogix5000Content/Controller/DataTypes/DataType", type.Value);
        var result = await service.PreviewStructuralEditsAsync(Request(protectedSource, protectedType, await Edit(protectedSource, "replace_element", Tag("Wire"), newWire))); Assert.False(result.CanApply); Assert.Contains(result.Findings, f => f.Check == "protected-layout-dependency");
    }
    [Theory] [InlineData("tag")] [InlineData("routine")] [InlineData("type")] [InlineData("alias")]
    public async Task Remaining_known_dependencies_block_incoherent_removals(string kind)
    {
        var text = kind == "alias" ? Source().Replace("<Tags>" + Wire, "<Tags><Tag Name='Alias' TagType='Alias' AliasFor='Wire'/>" + Wire) : Source().Replace("MOV(Keep,Keep)", "MOV(Wire,Wire)"); var p = await Register(text);
        var edit = kind switch { "routine" => await Edit(p, "remove_element", Routine("Old")), "type" => await Edit(p, "remove_element", "/RSLogix5000Content/Controller/DataTypes/DataType"), _ => await Edit(p, "remove_element", Tag("Wire")) };
        var preview = await service.PreviewStructuralEditsAsync(Request(p, edit)); Assert.False(preview.CanApply); Assert.Contains(preview.Findings, f => f.Status == "blocked"); await Assert.ThrowsAsync<OperationException>(() => service.ApplyStructuralEditsAsync(Apply(preview)));
    }
    [Fact]
    public async Task Protected_signed_encoded_force_and_declared_library_subtrees_cannot_be_changed()
    {
        var p = await Register(); var edit = await Edit(p, "remove_element", Routine("Library")); Assert.Equal("structural_protected", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(Request(p, edit)))).Code);
        var routine = await service.StructuralContextAsync(new(p.Id, p.CurrentSha256, 0, Routine("Main"))); var request = Request(p, await Edit(p, "set_text", MainText, "NOP();")); request = request with { Scope = request.Scope with { Protected = [new(Routine("Main"), routine.Targets[0].ElementSha256)] } };
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(request));
        var forced = await Register(Source().Replace("<ConsumeInfo", "<ForceData>0</ForceData><ConsumeInfo")); var forcedEdit = await Edit(forced, "remove_element", Tag("Wire")); await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(Request(forced, forcedEdit)));
        var newForce = Wire.Replace("<ConsumeInfo", "<ForceData>0</ForceData><ConsumeInfo"); var newForceEdit = await Edit(p, "replace_element", Tag("Wire"), newForce); await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(Request(p, newForceEdit)));
    }
    [Fact]
    public async Task Source_revision_node_plan_scope_and_retained_preview_hashes_are_required()
    {
        var p = await Register(); var edit = await Edit(p, "set_text", MainText, "MOV(Keep,Keep);"); var request = Request(p, edit); var preview = await service.PreviewStructuralEditsAsync(request);
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(request with { ExpectedRevision = 1 }));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(request with { Edits = [edit with { ExpectedElementSha256 = new string('0', 64) }] }));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(request with { Scope = request.Scope with { Routines = [] } }));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyStructuralEditsAsync(Apply(preview) with { ExpectedReviewSha256 = new string('0', 64) }));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyStructuralEditsAsync(Apply(preview) with { PreviewReceiptSha256 = new string('0', 64) }));
        File.AppendAllText(plan.Path!, "changed"); await Assert.ThrowsAsync<OperationException>(() => service.ApplyStructuralEditsAsync(Apply(preview)));
    }
    [Fact]
    public async Task Safety_and_incomplete_binding_acknowledgements_are_explicit_and_retained()
    {
        var p = await Register(Source().Replace("<Program Name='App'", "<Program Class='Safety' Name='App'")); var request = Request(p, await Edit(p, "set_text", MainText, "MOV(Keep,Keep);"));
        var blocked = await service.PreviewStructuralEditsAsync(request); Assert.False(blocked.CanApply); Assert.Contains(blocked.Findings, f => f.Check == "safety");
        request = request with { Scope = request.Scope with { SafetyHandling = "review-only", AcknowledgeIncompleteBindings = false } }; blocked = await service.PreviewStructuralEditsAsync(request); Assert.False(blocked.CanApply); Assert.Contains(blocked.Findings, f => f.Check == "binding-coverage");
        request = request with { Scope = request.Scope with { AcknowledgeIncompleteBindings = true } }; Assert.True((await service.PreviewStructuralEditsAsync(request)).CanApply);
    }
    [Fact]
    public async Task Completed_apply_is_reconciled_without_replaying_and_indeterminate_apply_stays_blocked()
    {
        var p = await Register(); var preview = await service.PreviewStructuralEditsAsync(Request(p, await Edit(p, "set_text", MainText, "MOV(Keep,Keep);"))); var result = await service.ApplyStructuralEditsAsync(Apply(preview));
        Assert.Equal(result.Project.Id, (await service.ApplyStructuralEditsAsync(Apply(preview))).Project.Id); Assert.Equal(2, service.Projects().Length);
        var next = await service.PreviewStructuralEditsAsync(Request(p, await Edit(p, "set_text", MainText, "NOP();"))); var path = Path.Combine(root, "state", "artifacts", next.PreviewReceipt!.Id, "structural-apply.json"); File.WriteAllText(path, JsonSerializer.Serialize(new { status = "applying-indeterminate", reviewSha256 = next.ReviewSha256 }, ProjectService.Json));
        Assert.Equal("structural_apply_indeterminate", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyStructuralEditsAsync(Apply(next)))).Code); Assert.Equal(2, service.Projects().Length);
    }
    [Fact]
    public async Task Structural_removal_preserves_internal_annotation_CRLF_CDATA_and_all_untouched_bytes()
    {
        var text = Source().Replace("<![CDATA[fresh]]>", "<![CDATA[line one\r\nline two\r\nμ]]>"); var p = await Register(text);
        var preview = await service.PreviewStructuralEditsAsync(Request(p, await Edit(p, "remove_element", Tag("Wire")))); Assert.True(preview.CanApply);
        var result = await service.ApplyStructuralEditsAsync(Apply(preview)); Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text.Replace(Wire, ""))), Revision(result.Project));
    }
    [Fact]
    public async Task Exact_reference_AOI_and_UDT_additions_preserve_existing_libraries_and_source_nodes()
    {
        const string type = "<DataType Name='AdapterData'><Members><Member Name='Status' DataType='DINT' Dimension='0'/></Members></DataType>";
        const string aoi = "<AddOnInstructionDefinition Name='Adapter' Class='Safety' SignatureID='reference-signature' Revision='1.360'><Parameters><Parameter Name='IO' DataType='AdapterData'/></Parameters><Routines><Routine Name='Logic' Type='RLL'><RLLContent><Rung Number='0'><Text><![CDATA[NOP();\r\n]]></Text></Rung></RLLContent></Routine></Routines></AddOnInstructionDefinition>";
        var reference = await Register("<RSLogix5000Content><Controller Name='Reference'><DataTypes>" + type + "</DataTypes><AddOnInstructionDefinitions>" + aoi + "</AddOnInstructionDefinitions></Controller></RSLogix5000Content>"); var p = await Register();
        var aoiContext = await service.StructuralContextAsync(new(reference.Id, reference.CurrentSha256, 0, "/RSLogix5000Content/Controller/AddOnInstructionDefinitions/AddOnInstructionDefinition"));
        var typeContext = await service.StructuralContextAsync(new(reference.Id, reference.CurrentSha256, 0, "/RSLogix5000Content/Controller/DataTypes/DataType"));
        StructuralReferenceAddition Aoi() => new("adapter", "aoi", "Adapter", new(reference.SourcePath, reference.CurrentSha256), aoiContext.Targets[0].XPath, aoiContext.Targets[0].ElementSha256);
        StructuralReferenceAddition Udt() => new("data", "udt", "AdapterData", new(reference.SourcePath, reference.CurrentSha256), typeContext.Targets[0].XPath, typeContext.Targets[0].ElementSha256);
        var addAoi = (await Edit(p, "add_reference", "/RSLogix5000Content/Controller/AddOnInstructionDefinitions")) with { ReferenceKey = "adapter" };
        var addType = (await Edit(p, "add_reference", "/RSLogix5000Content/Controller/DataTypes")) with { ReferenceKey = "data" };
        var request = Request(p, addAoi, addType); request = request with { Scope = request.Scope with { ReferenceAdditions = [Aoi(), Udt()], DataTypes = ["AdapterData"], SafetyHandling = "review-only" } };
        var preview = await service.PreviewStructuralEditsAsync(request); Assert.True(preview.CanApply); Assert.Equal(2, preview.ReferenceBindings.Length);
        var result = await service.ApplyStructuralEditsAsync(Apply(preview)); var expected = Source().Replace("</DataTypes>", type + "</DataTypes>").Replace("</AddOnInstructionDefinitions>", aoi + "</AddOnInstructionDefinitions>");
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(expected)), Revision(result.Project)); Assert.Equal(reference.SourceSha256, ProjectService.Hash(File.ReadAllBytes(reference.SourcePath)));
        var missing = request with { Edits = [addAoi], Scope = request.Scope with { ReferenceAdditions = [Aoi()] } }; var blocked = await service.PreviewStructuralEditsAsync(missing); Assert.False(blocked.CanApply); Assert.Contains(blocked.Findings, f => f.Check == "reference-type-dependency");
        var wrong = request with { Scope = request.Scope with { ReferenceAdditions = [Aoi() with { ExpectedElementSha256 = new string('0', 64) }, Udt()] } }; await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(wrong));
    }
    [Fact]
    public async Task Saved_state_dispositions_block_silent_reset_and_missing_decisions()
    {
        var p = await Register(); var remove = Request(p, await Edit(p, "remove_element", Tag("Wire"))); var missing = await service.PreviewStructuralEditsAsync(remove with { Scope = remove.Scope with { ValueDispositions = [] } }); Assert.False(missing.CanApply); Assert.Contains(missing.Findings, f => f.Check == "value-disposition");
        var element = XmlProjects.Select(XmlProjects.Parse(Revision(p)), Tag("Keep")).ToString(SaveOptions.DisableFormatting); var changed = element.Replace("41", "0"); var request = Request(p, await Edit(p, "replace_element", Tag("Keep"), changed)); request = request with { Scope = request.Scope with { ValueDispositions = [new(new("Keep"), "preserve", "Preserve the saved state exactly.")] } };
        var blocked = await service.PreviewStructuralEditsAsync(request); Assert.False(blocked.CanApply); Assert.Equal("blocked", Assert.Single(blocked.ValueDispositions).Status);
        request = request with { Scope = request.Scope with { ValueDispositions = [new(new("Keep"), "replace-reviewed", "Generated fixture explicit replacement; no automatic state rearming inference.")] } }; var reviewed = await service.PreviewStructuralEditsAsync(request); Assert.True(reviewed.CanApply); Assert.NotEqual(reviewed.ValueDispositions[0].BeforeValueSha256, reviewed.ValueDispositions[0].AfterValueSha256);
    }
    [Fact]
    public async Task Tampered_apply_state_cannot_be_reconciled_as_another_review()
    {
        var p = await Register(); var preview = await service.PreviewStructuralEditsAsync(Request(p, await Edit(p, "set_text", MainText, "NOP();"))); var path = Path.Combine(root, "state", "artifacts", preview.PreviewReceipt!.Id, "structural-apply.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { status = "applying-indeterminate", reviewSha256 = new string('0', 64) }, ProjectService.Json));
        Assert.Equal("state_tampered", (await Assert.ThrowsAsync<OperationException>(() => service.StructuralEditStatusAsync(preview.PreviewReceipt.Id, preview.PreviewReceipt.Sha256))).Code);
        Assert.Equal("state_tampered", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyStructuralEditsAsync(Apply(preview)))).Code);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
