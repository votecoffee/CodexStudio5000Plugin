using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ProjectServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexStudio5000Tests", Guid.NewGuid().ToString("N"));
    private readonly string input;
    private readonly ServiceOptions options;
    private ProjectService service;
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.00" ExportDate="fixed"><Controller Name="Test">
        <DataTypes/><Tags><Tag Name="Count" TagType="Base" DataType="DINT"><Description><![CDATA[Before]]></Description></Tag></Tags>
        <Programs><Program Name="Main"><Tags/><Routines>
        <Routine Name="Logic" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine>
        <Routine Name="Compute" Type="ST"><STContent><Line Number="0"><![CDATA[Count := Count + 1;]]></Line></STContent></Routine>
        </Routines></Program></Programs><Tasks/></Controller></RSLogix5000Content>
        """;

    public ProjectServiceTests()
    {
        Directory.CreateDirectory(root); input = Path.Combine(root, "source.l5x"); File.WriteAllText(input, Xml);
        options = new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] };
        service = new(options);
    }

    [Fact]
    public async Task PreviewApplyExportPreservesOriginalBytesAndSurvivesRestart()
    {
        var original = File.ReadAllBytes(input);
        var project = await service.RegisterAsync(input);
        var preview = await service.PreviewAsync(project.Id, new(project.CurrentSha256,
            [new("set_text", "//Tag[@Name='Count']/Description", "Count – completed cycles 零")]));
        Assert.Equal(project, service.Get(project.Id));
        var updated = await service.ApplyAsync(project.Id, preview.Id, new(project.CurrentSha256));
        Assert.Equal(1, updated.Revision);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(options.StateDirectory, "projects", project.Id, "original.l5x")));
        Assert.Equal("Count – completed cycles 零", XDocument.Parse(service.ReadElement(project.Id, "//Tag/Description").Xml).Root!.Value);
        Assert.Equal(updated, await service.ApplyAsync(project.Id, preview.Id, new(project.CurrentSha256)));
        service.Dispose(); service = new(options);
        Assert.Equal(updated, service.Get(project.Id));
        var export = await service.ExportAsync(project.Id, new(updated.CurrentSha256));
        Assert.Equal(updated.CurrentSha256, export.Sha256);
        Assert.Contains(service.History(project.Id), e => e.Action == "apply");
    }

    [Fact]
    public async Task TwoClientsCannotApplyConflictingPreviews()
    {
        var p = await service.RegisterAsync(input);
        var a = await service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("set_text", "//Description", "A")]));
        var b = await service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("set_text", "//Description", "B")]));
        var results = await Task.WhenAll(new[] { a, b }.Select(async draft =>
        {
            try { await service.ApplyAsync(p.Id, draft.Id, new(p.CurrentSha256)); return "ok"; }
            catch (OperationException e) { return e.Code; }
        }));
        Assert.Single(results, r => r == "ok"); Assert.Single(results, r => r == "stale_revision");
    }

    [Fact]
    public async Task BlocksMalformedXmlDtdDuplicateNameAndBroadSelectors()
    {
        var p = await service.RegisterAsync(input);
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => service.PreviewAsync(p.Id, new(p.CurrentSha256,
            [new("replace_element", "//Tag", "<Tag>")])));
        var duplicate = await Assert.ThrowsAsync<OperationException>(() => service.PreviewAsync(p.Id, new(p.CurrentSha256,
            [new("add_element", "/RSLogix5000Content/Controller/Tags", "<Tag Name='count'/>")])));
        Assert.Equal("duplicate_name", duplicate.Code);
        var ambiguous = await Assert.ThrowsAsync<OperationException>(() => service.PreviewAsync(p.Id, new(p.CurrentSha256,
            [new("remove_element", "//Routine")])));
        Assert.Equal("ambiguous_target", ambiguous.Code);
        Assert.Throws<System.Xml.XmlException>(() => XmlProjects.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/secret'>]><RSLogix5000Content><Controller>&e;</Controller></RSLogix5000Content>")));
        Assert.Equal(p.CurrentSha256, service.Get(p.Id).CurrentSha256);
    }

    [Fact]
    public async Task RejectsSourceOutsideAllowedRootAndWorkingCopyTampering()
    {
        var narrow = new ServiceOptions { StateDirectory = Path.Combine(root, "other-state"), AllowedRoots = [Path.Combine(root, "allowed")] };
        using var other = new ProjectService(narrow);
        var denied = await Assert.ThrowsAsync<OperationException>(() => other.RegisterAsync(input));
        Assert.Equal("path_denied", denied.Code);
        Assert.Throws<OperationException>(() => service.Get("../../outside"));
        var p = await service.RegisterAsync(input);
        File.AppendAllText(Path.Combine(options.StateDirectory, "projects", p.Id, "revision-0.l5x"), " ");
        Assert.Equal("state_tampered", Assert.Throws<OperationException>(() => service.Get(p.Id)).Code);
    }

    [Fact]
    public async Task AddReplaceRemoveAndAttributeEditsWorkAsOneRevision()
    {
        var p = await service.RegisterAsync(input);
        var edits = new Edit[] {
            new("set_attribute", "//Tag[@Name='Count']", "REAL", "DataType"),
            new("replace_element", "//Routine[@Name='Logic']", "<Routine Name='Logic' Type='RLL'><RLLContent><Rung Number='0' Type='N'><Text><![CDATA[XIC(Ready)OTE(Run);]]></Text></Rung></RLLContent></Routine>"),
            new("add_element", "/RSLogix5000Content/Controller/Tags", "<Tag Name='Ready' TagType='Base' DataType='BOOL'/>")
        };
        var preview = await service.PreviewAsync(p.Id, new(p.CurrentSha256, edits));
        var result = await service.ApplyAsync(p.Id, preview.Id, new(p.CurrentSha256));
        Assert.Contains("REAL", service.ReadElement(p.Id, "//Tag[@Name='Count']").Xml);
        Assert.Contains("XIC(Ready)", service.ReadElement(p.Id, "//Routine[@Name='Logic']").Xml);
        var remove = await service.PreviewAsync(p.Id, new(result.CurrentSha256, [new("remove_element", "//Tag[@Name='Ready']")]));
        await service.ApplyAsync(p.Id, remove.Id, new(result.CurrentSha256));
        Assert.Empty(service.Inspect(p.Id, "Tag", "Ready").Elements);
    }

    [Fact]
    public async Task ExplodedRoundtripRetainsLogicAndPreservesExportDate()
    {
        var p = await service.RegisterAsync(input);
        var artifact = await service.ExportAsync(p.Id, new(p.CurrentSha256, "explode"));
        var (_, zip) = service.Download(artifact.Id);
        var extracted = Path.Combine(root, "exploded");
        ZipFile.ExtractToDirectory(zip, extracted);
        var roundtrip = await service.RegisterExplodedAsync(extracted);
        Assert.Contains("Count := Count + 1;", service.ReadElement(roundtrip.Id, "//Routine[@Name='Compute']").Xml);
        Assert.Contains("NOP();", service.ReadElement(roundtrip.Id, "//Routine[@Name='Logic']").Xml);
        Assert.Contains("ExportDate=\"fixed\"", service.ReadElement(roundtrip.Id, "/RSLogix5000Content").Xml);
    }

    [Fact]
    public async Task NativeUnavailableFailsExplicitlyAndDoesNotChangeProject()
    {
        var p = await service.RegisterAsync(input);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ExportAsync(p.Id, new(p.CurrentSha256, "acd")));
        Assert.Equal("native_unavailable", error.Code); Assert.Equal(p, service.Get(p.Id));
    }

    [Fact]
    public void SecondServiceCannotOwnTheSameState() => Assert.Throws<IOException>(() => new ProjectService(options));

    [Fact]
    public async Task RepeatedDependencyReferencesAreNotDuplicateDeclarations()
    {
        File.WriteAllText(input, Xml.Replace("<DataTypes/>", "<DataTypes/><Dependencies><Dependency Name='STRING004'/><Dependency Name='STRING004'/></Dependencies>"));
        var p = await service.RegisterAsync(input);
        Assert.Equal(0, p.Revision);
    }

    [Fact]
    public async Task NativeEmptyLadderRoutineWithoutContentContainerCanBeRegisteredAndEdited()
    {
        File.WriteAllText(input, "<RSLogix5000Content><Controller><Programs><Program Name='CodexTest'><Routines><Routine Name='main' Type='RLL'/></Routines></Program></Programs></Controller></RSLogix5000Content>");
        var original = File.ReadAllBytes(input); var project = await service.RegisterAsync(input);
        var preview = await service.PreviewAsync(project.Id, new(project.CurrentSha256, [new("replace_element", "//Routine[@Name='main']", "<Routine Name='main' Type='RLL'><RLLContent><Rung Number='0' Type='N'><Text>NOP();</Text></Rung></RLLContent></Routine>")]));
        await service.ApplyAsync(project.Id, preview.Id, new(project.CurrentSha256));
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Contains("NOP();", service.ReadElement(project.Id, "//Routine[@Name='main']").Xml);
        var invalid = "<RSLogix5000Content><Controller><Programs><Program Name='P'><Routines><Routine Name='R' Type='RLL'><STContent/></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
        Assert.Equal("invalid_routine", Assert.Throws<OperationException>(() => XmlProjects.Parse(Encoding.UTF8.GetBytes(invalid))).Code);
    }

    [Fact]
    public async Task SavingArtifactsCannotOverwriteOrEscapeOutputRoots()
    {
        var p = await service.RegisterAsync(input);
        var artifact = await service.ExportAsync(p.Id, new(p.CurrentSha256));
        var denied = await Assert.ThrowsAsync<OperationException>(() => service.SaveArtifactAsync(artifact.Id, input));
        Assert.Equal("path_denied", denied.Code);
    }

    public void Dispose() => service.Dispose(); // retain unique test state for investigation
}
