using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Extensions.Rockwell;
using Xunit;

namespace Studio5000.Tests;

public sealed class GraphicsResolutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellResolutionTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private const string Plc = """
        <RSLogix5000Content><Controller Name="Generated"><DataTypes>
        <DataType Name="Record"><Members><Member Name="Count" DataType="DINT"/><Member Name="Samples" DataType="INT" Dimension="3"/>
        <Member Name="Secret" DataType="DINT" Hidden="true"/></Members></DataType></DataTypes>
        <Tags><Tag Name="Records" DataType="Record" Dimensions="2"/><Tag Name="Matrix" DataType="DINT" Dimensions="2,3"/>
        <Tag Name="Bits" DataType="DINT"/><Tag Name="Timer" DataType="TIMER"/>
        <Tag Name="Alias" TagType="Alias" AliasFor="Bits"/></Tags>
        <Programs><Program Name="Example"><Tags><Tag Name="Local" DataType="Record"/></Tags></Program></Programs>
        </Controller></RSLogix5000Content>
        """;

    public GraphicsResolutionTests()
    {
        Directory.CreateDirectory(root);
        service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], FactoryTalkSchemaDirectory = Path.Combine(root, "no-schemas") });
    }
    private async Task<ProjectInfo> Register(string content, string extension)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + extension); File.WriteAllText(path, content); return await service.RegisterAsync(path);
    }
    private static string Graphics(string expression, string edition = "ME") => new XElement("gfx",
        new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
        new XAttribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "noNamespaceSchemaLocation", $"Gfx-{edition}17.xsd"),
        new XElement("group", new XAttribute("name", "First"), new XElement("numericDisplay", new XAttribute("name", "Value"), new XAttribute("expression", expression)))).ToString();
    private static GraphicsResolveRequest Request(ProjectInfo gfx, ProjectInfo plc, params GraphicsParameterBinding[] parameters) =>
        new(gfx.CurrentSha256, [new("PLC", plc.Id, plc.CurrentSha256)], parameters);
    private async Task<GraphicsResolvedReference> Resolve(string expression)
    {
        var gfx = await Register(Graphics(expression), ".xml"); var plc = await Register(Plc, ".l5x");
        return Assert.Single(service.ResolveGraphicsReferences(gfx.Id, Request(gfx, plc)).References);
    }

    [Theory]
    [InlineData("{[plc]program:example.local.samples[2]}", "INT")]
    [InlineData("{::[PLC]Records[1].Count}", "DINT")]
    [InlineData("{[PLC]Matrix[1,2].31}", "BOOL")]
    [InlineData("{[PLC]Bits.0}", "BOOL")]
    public async Task ResolvesScopedTagsMembersMultidimensionalArraysAndIntegerBits(string expression, string type)
    {
        var result = await Resolve(expression); Assert.Equal("matched_in_snapshot", result.Status); Assert.Equal(type, result.DataType);
        Assert.NotNull(result.ProjectSha256); Assert.Empty(result.Dimensions!);
    }
    [Theory]
    [InlineData("{[PLC]Records[2].Count}", "index_out_of_range")]
    [InlineData("{[PLC]Records[999999999999999999].Count}", "index_out_of_range")]
    [InlineData("{[PLC]Records.Count}", "array_index_required")]
    [InlineData("{[PLC]Matrix[1]}", "array_rank_mismatch")]
    [InlineData("{[PLC]Bits.32}", "bit_out_of_range")]
    [InlineData("{[PLC]Timer.PRE}", "type_metadata_unavailable")]
    [InlineData("{[PLC]Alias}", "alias_requires_resolution")]
    [InlineData("{[PLC]Records[0].Unknown}", "member_absent_in_snapshot")]
    [InlineData("{[PLC]Records[0].Secret}", "member_not_exposed")]
    [InlineData("{[PLC]Absent}", "tag_absent_in_snapshot")]
    [InlineData("{[PLC]Program:Other.Local}", "program_absent_in_snapshot")]
    [InlineData("{[PLC]Records[Index].Count}", "unsupported_logix_address")]
    [InlineData("{[PLC]Local:1:I.Data}", "unsupported_logix_address")]
    [InlineData("{[Other]Bits}", "requires_shortcut_mapping")]
    [InlineData("HmiDatabaseTag", "requires_hmi_database")]
    [InlineData("{#1.Count}", "requires_parameter_context")]
    public async Task IncompleteContextAndUnsupportedAddressesAreSeparateFromSnapshotAbsence(string expression, string status) => Assert.Equal(status, (await Resolve(expression)).Status);

    [Theory]
    [InlineData("ME")]
    [InlineData("SE")]
    public async Task NearestExplicitScopeKeepsRepeatedGlobalObjectInstancesIndependent(string edition)
    {
        var document = XDocument.Parse(Graphics("{#1.Count}", edition));
        document.Root!.Add(new XElement("group", new XAttribute("name", "Second"), new XElement("numericDisplay", new XAttribute("name", "Value"), new XAttribute("expression", "{#1.Count}"))));
        var gfx = await Register(document.ToString(), ".xml"); var plc = await Register(Plc, ".l5x");
        var request = Request(gfx, plc, new("/gfx[1]", "#1", "{[PLC]Records[0]}"), new("/gfx[1]/group[@name='Second']", "#1", "{[PLC]Program:Example.Local}"));
        var report = service.ResolveGraphicsReferences(gfx.Id, request);
        Assert.Equal(2, report.Total); Assert.All(report.References, r => Assert.Equal("matched_in_snapshot", r.Status));
        Assert.Equal("[PLC]Records[0].Count", report.References[0].ExpandedReference);
        Assert.Equal("[PLC]Program:Example.Local.Count", report.References[1].ExpandedReference);
        Assert.Equal("/gfx[1]:#1", Assert.Single(report.References[0].ParameterTrace));
        Assert.Equal("/gfx[1]/group[@name='Second']:#1", Assert.Single(report.References[1].ParameterTrace));
        var filtered = service.ResolveGraphicsReferences(gfx.Id, request with { Query = "program:example", Limit = 1 });
        Assert.Single(filtered.References); Assert.Equal(1, filtered.Total);
    }
    [Fact]
    public async Task RecursiveParametersSupportShortcutFragmentsAndCyclesAreBounded()
    {
        var gfx = await Register(Graphics("{[#1]Program:Example.#10.Count}"), ".xml"); var plc = await Register(Plc, ".l5x");
        var request = Request(gfx, plc, new("/gfx[1]", "#1", "PLC"), new("/gfx[1]", "#10", "#2"), new("/gfx[1]", "#2", "Local"));
        Assert.Equal("matched_in_snapshot", Assert.Single(service.ResolveGraphicsReferences(gfx.Id, request).References).Status);
        request = request with { Parameters = [new("/gfx[1]", "#1", "#2"), new("/gfx[1]", "#2", "#1")] };
        Assert.Equal("parameter_cycle", Assert.Single(service.ResolveGraphicsReferences(gfx.Id, request).References).Status);
        request = request with { Parameters = Enumerable.Range(1, 20).Select(n => new GraphicsParameterBinding("/gfx[1]", "#" + n, "#" + (n + 1))).ToArray() };
        Assert.Equal("parameter_expansion_limit", Assert.Single(service.ResolveGraphicsReferences(gfx.Id, request).References).Status);
    }
    [Fact]
    public async Task ExportParameterValuesAreNotAssumedToBeInstanceContext()
    {
        var doc = XDocument.Parse(Graphics("{#1.Count}"));
        doc.Root!.Element("group")!.Add(new XElement("parameters", new XElement("parameter", new XAttribute("name", "#1"), new XAttribute("value", "{[PLC]Records[0]}"))));
        var gfx = await Register(doc.ToString(), ".xml"); var plc = await Register(Plc, ".l5x");
        var report = service.ResolveGraphicsReferences(gfx.Id, Request(gfx, plc));
        Assert.Equal("requires_parameter_context", report.References[0].Status); Assert.Equal("matched_in_snapshot", report.References[1].Status);
    }
    [Fact]
    public async Task WholeArrayMetadataPaginationAndSourcesArePreserved()
    {
        var gfx = await Register(Graphics("{[PLC]Records} + {[PLC]Records} + {[PLC]Bits}"), ".xml"); var plc = await Register(Plc, ".l5x");
        var before = File.ReadAllBytes(plc.SourcePath); var request = Request(gfx, plc) with { Offset = 1, Limit = 1 };
        File.WriteAllText(gfx.SourcePath, "changed outside the managed snapshot");
        var report = service.ResolveGraphicsReferences(gfx.Id, request);
        Assert.Equal(3, report.Total); Assert.Equal(new[] { 2 }, Assert.Single(report.References).Dimensions);
        Assert.Equal(before, File.ReadAllBytes(plc.SourcePath)); Assert.Equal(gfx, service.Get(gfx.Id)); Assert.Equal(plc, service.Get(plc.Id));
    }
    [Fact]
    public async Task HashesAreRequiredForBothDisplayAndEveryMappedSnapshot()
    {
        var gfx = await Register(Graphics("{[PLC]Bits}"), ".xml"); var plc = await Register(Plc, ".l5x"); var request = Request(gfx, plc);
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { ExpectedSha256 = "stale" })).Code);
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Shortcuts = [new("PLC", plc.Id, "stale")] })).Code);
        Assert.Equal("not_logix", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Shortcuts = [new("PLC", gfx.Id, gfx.CurrentSha256)] })).Code);
    }
    [Fact]
    public async Task InvalidOrAmbiguousContextFailsBeforeResolving()
    {
        var gfx = await Register(Graphics("{#1.Count}"), ".xml"); var plc = await Register(Plc, ".l5x"); var request = Request(gfx, plc);
        Assert.Equal("invalid_resolution_context", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Shortcuts = [request.Shortcuts[0], request.Shortcuts[0] with { Shortcut = "plc" }] })).Code);
        Assert.Equal("parameter_scope_missing", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Parameters = [new("//group", "#1", "[PLC]Records[0]")] })).Code);
        Assert.Equal("invalid_resolution_context", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Parameters = [new("/gfx[1]", "#1", "x"), new("/gfx[1]", "#1", "y")] })).Code);
        Assert.Equal("invalid_resolution_context", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Shortcuts = null! })).Code);
        Assert.Equal("invalid_resolution_context", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Parameters = [null!] })).Code);
    }
    [Fact]
    public void NewReadOperationAcceptsNestedContextUsingTheSharedJsonContract()
    {
        var schema = new FactoryTalkExtension().Describe().Single(o => o.Name == "ftview_resolve_references").InputSchema;
        ExtensionSchema.Validate(schema, JsonSerializer.SerializeToElement(new { projectId = "display", request = new GraphicsResolveRequest("hash", [new("PLC", "plc", "hash")], [new("/gfx[1]", "#1", "Tag")]) }, ProjectService.Json));
    }
    [Fact]
    public async Task ResultPaginationIncludesOccurrencesBeyondTheFirstExtractionPage()
    {
        var gfx = await Register(Graphics(string.Join(" + ", Enumerable.Repeat("{[PLC]Bits}", 220))), ".xml");
        var plc = await Register(Plc, ".l5x");
        var page = service.ResolveGraphicsReferences(gfx.Id, Request(gfx, plc) with { Offset = 215, Limit = 3 });
        Assert.Equal(220, page.Total); Assert.Equal(3, page.References.Length);
        Assert.All(page.References, r => Assert.Equal("matched_in_snapshot", r.Status));
        Assert.Contains("/gfx[1]", page.ParameterScopes);
    }
    [Fact]
    public async Task ScopesWithQuotesAndSlashesUseCanonicalIdentityAndUnnamedExpressionsInheritDisplayBindings()
    {
        var document = XDocument.Parse(Graphics("{#1.Count}"));
        var group = document.Root!.Element("group")!; group.SetAttributeValue("name", "Mixer's / \"detail\"");
        document.Root.Add(new XElement("connection", new XAttribute("expression", "{#2}")));
        var gfx = await Register(document.ToString(), ".xml"); var plc = await Register(Plc, ".l5x");
        var groupPath = FactoryTalkGraphics.StablePath(group);
        var result = service.ResolveGraphicsReferences(gfx.Id, Request(gfx, plc, new(groupPath, "#1", "[PLC]Records[0]"), new("/gfx[1]", "#2", "[PLC]Bits")));
        Assert.All(result.References, r => Assert.Equal("matched_in_snapshot", r.Status)); Assert.Contains(groupPath, result.ParameterScopes);
    }
    [Fact]
    public async Task ContextAndExpansionSizeLimitsAreEnforced()
    {
        var gfx = await Register(Graphics("{#1}"), ".xml"); var plc = await Register(Plc, ".l5x"); var request = Request(gfx, plc);
        Assert.Equal("invalid_resolution_context", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Limit = 201 })).Code);
        Assert.Equal("invalid_resolution_context", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Parameters = [new("/gfx[1]", "#1", new string('x', 1025))] })).Code);
        Assert.Equal("invalid_resolution_context", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request with { Shortcuts = Enumerable.Range(0,65).Select(n => new GraphicsShortcutBinding("S" + n, plc.Id, plc.CurrentSha256)).ToArray() })).Code);
        var tooLong = service.ResolveGraphicsReferences(gfx.Id, request with { Parameters = [new("/gfx[1]", "#1", "#2#2#2#2#2"), new("/gfx[1]", "#2", new string('x', 1000))] });
        Assert.Equal("parameter_expansion_limit", Assert.Single(tooLong.References).Status);
    }
    [Fact]
    public async Task EncodedResponseBudgetRejectsLargePagesAndAllowsSmallerOnes()
    {
        var document = XDocument.Parse(Graphics("{[PLC]Bits} + \"" + new string('<', 10000) + "\""));
        var group = document.Root!.Element("group")!; var template = group.Element("numericDisplay")!;
        for (var i = 1; i < 100; i++) { var copy = new XElement(template); copy.SetAttributeValue("name", "Value" + i); group.Add(copy); }
        var gfx = await Register(document.ToString(), ".xml"); var plc = await Register(Plc, ".l5x"); var request = Request(gfx, plc);
        Assert.Equal("reference_page_too_large", Assert.Throws<OperationException>(() => service.ResolveGraphicsReferences(gfx.Id, request)).Code);
        var page = service.ResolveGraphicsReferences(gfx.Id, request with { Limit = 1 }); Assert.Single(page.References); Assert.Equal(100, page.Total);
    }
    public void Dispose() => service.Dispose();
}
