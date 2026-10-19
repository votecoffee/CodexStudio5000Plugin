using System.IO.Compression;
using System.Text;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ExplodedCompatibilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellExplodedTests", Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly ProjectService service;
    private const string Xml = """
        <RSLogix5000Content><Controller Name="Example"><DataTypes/><AddOnInstructionDefinitions/>
        <Tags><Tag Name="Count" DataType="DINT"/></Tags><Programs><Program Name="ExampleProgram"><Tags/><Routines>
        <Routine Name="Calculate" Type="ST"><STContent><Line Number="0"><![CDATA[Count := 1;]]></Line></STContent></Routine>
        </Routines></Program></Programs><Tasks/></Controller></RSLogix5000Content>
        """;

    public ExplodedCompatibilityTests()
    {
        Directory.CreateDirectory(root);
        source = Path.Combine(root, "input.l5x");
        File.WriteAllText(source, Xml);
        service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }

    private async Task<string> ExportTree()
    {
        var project = await service.RegisterAsync(source);
        var artifact = await service.ExportAsync(project.Id, new(project.CurrentSha256, "explode"));
        var (_, zip) = service.Download(artifact.Id);
        var tree = Path.Combine(root, Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zip, tree);
        return tree;
    }

    private static string Options(string tree) => Path.Combine(tree, "RSLogix5000Content", "export-options.yaml");

    [Fact]
    public async Task ExportStampsActualLegacyLayoutAndImportPreservesSourceTree()
    {
        var tree = await ExportTree();
        Assert.StartsWith("schema_version: 1\n", File.ReadAllText(Options(tree)));
        var before = Directory.GetFiles(tree, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => ProjectService.Hash(File.ReadAllBytes(f)));
        var result = await service.RegisterExplodedAsync(tree);
        Assert.Contains("Count := 1;", service.ReadElement(result.Id, "//Routine[@Name='Calculate']").Xml);
        Assert.All(before, item => Assert.Equal(item.Value, ProjectService.Hash(File.ReadAllBytes(item.Key))));
        Assert.Equal(Xml, File.ReadAllText(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnstampedLegacyDirectoriesRemainReadable(bool omitOptionsFile)
    {
        var tree = await ExportTree();
        if (omitOptionsFile) File.Delete(Options(tree));
        else File.WriteAllText(Options(tree), File.ReadAllText(Options(tree)).Replace("schema_version: 1\n", ""));
        var result = await service.RegisterExplodedAsync(tree);
        Assert.Contains("Count := 1;", service.ReadElement(result.Id, "//Routine[@Name='Calculate']").Xml);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("unknown")]
    public async Task UnsupportedStampIsRejectedBeforeXmlIsReadOrProjectPublished(string version)
    {
        var tree = await ExportTree();
        File.WriteAllText(Options(tree), "schema_version: " + version + "\nserialization_format: Xml\n");
        File.WriteAllText(Path.Combine(tree, "RSLogix5000Content", "RSLogix5000Content.xml"), "<!DOCTYPE x><broken");
        var before = service.Projects().Count();
        var error = await Assert.ThrowsAsync<OperationException>(() => service.RegisterExplodedAsync(tree));
        Assert.Equal("unsupported_exploded_schema", error.Code);
        Assert.Equal(before, service.Projects().Count());
        Assert.Equal(Xml, File.ReadAllText(source));
    }

    [Theory]
    [InlineData("schema_version: 1\nschema_version: 2\n")]
    [InlineData("schema_version: [1]\n")]
    [InlineData("schema_version: &version 1\nserialization_format: *version\n")]
    [InlineData("schema_version: 1\nfuture_option: true\n")]
    [InlineData("schema_version: 1\nserialization_format: Json\n")]
    [InlineData("schema_version: 1\nomit_export_date: perhaps\n")]
    [InlineData("schema_version: 1\nunsafe_skip_dependency_check: true\n")]
    [InlineData("---\nschema_version: 1\n---\nschema_version: 1\n")]
    public async Task AmbiguousOrUnsupportedOptionsAreRejected(string yaml)
    {
        var tree = await ExportTree();
        File.WriteAllText(Options(tree), yaml);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.RegisterExplodedAsync(tree));
        Assert.Equal("invalid_exploded_options", error.Code);
    }

    [Fact]
    public async Task OversizedOptionsAreRejectedWithoutPublishingAProject()
    {
        var tree = await ExportTree();
        File.WriteAllText(Options(tree), "#" + new string('x', 64 * 1024));
        var count = service.Projects().Count();
        var error = await Assert.ThrowsAsync<OperationException>(() => service.RegisterExplodedAsync(tree));
        Assert.Equal("invalid_exploded_options", error.Code);
        Assert.Equal(count, service.Projects().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ExportOptions=\"Dependencies\"")]
    public async Task ProtectedAoisCannotBeExplodedEvenWithNewerDependencyMetadata(string exportOptions)
    {
        var xml = Xml.Replace("<RSLogix5000Content>", "<RSLogix5000Content" + exportOptions + ">")
            .Replace("<AddOnInstructionDefinitions/>", "<AddOnInstructionDefinitions><EncodedData Name='Protected'><Dependencies/></EncodedData></AddOnInstructionDefinitions>");
        File.WriteAllText(source, xml);
        var project = await service.RegisterAsync(source);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ExportAsync(project.Id, new(project.CurrentSha256, "explode")));
        Assert.Equal("unsupported_exploded_aoi", error.Code);
        var ordinary = await service.ExportAsync(project.Id, new(project.CurrentSha256, "l5x"));
        Assert.Equal(ProjectService.Hash(Encoding.UTF8.GetBytes(xml)), ordinary.Sha256);
        Assert.Equal(xml, File.ReadAllText(source));
        Assert.Equal(project, service.Get(project.Id));
    }

    [Fact]
    public async Task UnstampedImportCannotBypassProtectedAoiGuard()
    {
        var tree = await ExportTree();
        File.Delete(Options(tree));
        var file = Path.Combine(tree, "RSLogix5000Content", "protected.xml");
        File.WriteAllText(file, "<EncodedData Name='Protected'><Dependencies/></EncodedData>");
        var error = await Assert.ThrowsAsync<OperationException>(() => service.RegisterExplodedAsync(tree));
        Assert.Equal("unsupported_exploded_aoi", error.Code);
    }

    [Theory]
    [InlineData("<Description>Keep this description</Description>")]
    [InlineData("<!-- Keep this comment -->")]
    public async Task StRoutineMetadataIsRejectedRatherThanSilentlyDiscarded(string metadata)
    {
        var xml = Xml.Replace("<STContent>", metadata + "<STContent>");
        File.WriteAllText(source, xml);
        var project = await service.RegisterAsync(source);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.ExportAsync(project.Id, new(project.CurrentSha256, "explode")));
        Assert.Equal("unsupported_exploded_metadata", error.Code);
        Assert.Equal(xml, File.ReadAllText(source));
        Assert.Equal(project, service.Get(project.Id));
    }

    public void Dispose() => service.Dispose();
}
