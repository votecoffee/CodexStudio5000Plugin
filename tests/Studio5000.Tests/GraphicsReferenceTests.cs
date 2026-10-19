using System.Xml.Linq;
using System.Xml.XPath;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class GraphicsReferenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellReferenceTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;

    public GraphicsReferenceTests()
    {
        Directory.CreateDirectory(root);
        service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root],
            FactoryTalkSchemaDirectory = Path.Combine(root, "schemas-not-installed") });
    }

    private async Task<(ProjectInfo Project, string Path, XDocument Document)> Fixture(string expression, string edition = "ME")
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var doc = new XDocument(new XElement("gfx", new XAttribute(XNamespace.Xmlns + "xsi", xsi),
            new XAttribute(xsi + "noNamespaceSchemaLocation", $"Gfx-{edition}17.xsd"),
            new XElement("group", new XAttribute("name", "Mixer's \"detail\""),
                new XElement("numericDisplay", new XAttribute("name", "Count"),
                    new XElement("connections", new XElement("connection", new XAttribute("name", "Value"), new XAttribute("expression", expression)))))));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".xml");
        doc.Save(path);
        return (await service.RegisterAsync(path), path, doc);
    }

    [Theory]
    [InlineData("ME")]
    [InlineData("SE")]
    public async Task MultipleReferencesRetainOccurrencesArraysAndSourceSelectors(string edition)
    {
        const string expression = "{[PLC]Program:ExampleProgram.Values[2].Count} + {[PLC]Other} + {[PLC]Other}";
        var (project, path, doc) = await Fixture(expression, edition);
        var before = ProjectService.Hash(File.ReadAllBytes(path));
        var report = service.GraphicsTagReferences(project.Id, new(project.CurrentSha256));
        Assert.Equal(3, report.Total);
        Assert.All(report.References, item =>
        {
            Assert.Equal(expression, item.Expression);
            Assert.Equal("Count", item.ObjectName);
            Assert.Equal("PLC", item.Shortcut);
            Assert.Equal("requires_shortcut_mapping", item.Resolution);
            Assert.Equal(expression, doc.XPathEvaluate("string(" + item.XPath + ")"));
            Assert.Single(doc.XPathSelectElements(item.ObjectXPath));
            Assert.Equal("{" + item.Reference + "}", expression.Substring(item.Start, item.Length));
        });
        Assert.Equal("Program:ExampleProgram.Values[2].Count", report.References[0].TagPath);
        Assert.NotEqual(report.References[1].Start, report.References[2].Start);
        Assert.Equal(before, ProjectService.Hash(File.ReadAllBytes(path)));
        Assert.Equal(project, service.Get(project.Id));
    }

    [Fact]
    public async Task FiltersAndPagesWithoutLosingTheBoundSnapshotHash()
    {
        var (project, _, _) = await Fixture("{[PLC]Alpha} + {[PLC]Beta} + {[PLC]Beta}");
        var page = service.GraphicsTagReferences(project.Id, new(project.CurrentSha256, "bEtA", 1, 1));
        Assert.Equal(2, page.Total);
        Assert.Equal(1, page.Offset);
        Assert.Equal("Beta", Assert.Single(page.References).TagPath);
        Assert.Equal(project.CurrentSha256, page.Project.CurrentSha256);
        Assert.Empty(service.GraphicsTagReferences(project.Id, new(project.CurrentSha256, "absent")).References);
        Assert.Equal(3, service.GraphicsTagReferences(project.Id, new(project.CurrentSha256, "Count")).Total);
    }

    [Theory]
    [InlineData("{#1.Member}", "parameter", "requires_parameter_context")]
    [InlineData("#2.Member", "parameter", "requires_parameter_context")]
    [InlineData("{Area\\Motor-Speed}", "hmi", "requires_hmi_database")]
    [InlineData("HmiTag", "hmi", "requires_hmi_database")]
    [InlineData("{[PLC]}", "unresolved", "invalid_shortcut_syntax")]
    public async Task MissingContextRemainsExplicitRatherThanAMissingTagVerdict(string expression, string kind, string resolution)
    {
        var (project, _, _) = await Fixture(expression);
        var reference = Assert.Single(service.GraphicsTagReferences(project.Id, new(project.CurrentSha256)).References);
        Assert.Equal(kind, reference.Kind);
        Assert.Equal(resolution, reference.Resolution);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("FALSE")]
    [InlineData("\"literal {[PLC]NotATag}\"")]
    [InlineData("\"literal \"\"{[PLC]NotATag}\"\"\"")]
    public async Task ConstantsAndQuotedLiteralBracesAreNotTagReferences(string expression)
    {
        var (project, _, _) = await Fixture(expression);
        Assert.Empty(service.GraphicsTagReferences(project.Id, new(project.CurrentSha256)).References);
    }

    [Theory]
    [InlineData("{[PLC]Good} + {bad", "malformed_braced_reference")]
    [InlineData("\"unterminated {[PLC]NotATag}", "unterminated_string_literal")]
    [InlineData("HmiTag + 1", "complex_unbraced_expression_not_resolved")]
    public async Task UnsupportedExpressionsRetainDiagnostics(string expression, string expected)
    {
        var (project, _, _) = await Fixture(expression);
        var report = service.GraphicsTagReferences(project.Id, new(project.CurrentSha256));
        Assert.Contains(report.Diagnostics, d => d.StartsWith(expected + ":", StringComparison.Ordinal));
        Assert.False(report.DiagnosticsTruncated);
    }

    [Fact]
    public async Task ParameterValuesAreInventoriedWithoutSubstitutingTheConnectionExpression()
    {
        var (_, path, document) = await Fixture("{#1.Member}");
        document.Descendants("group").Single().Add(new XElement("parameters",
            new XElement("parameter", new XAttribute("name", "#1"), new XAttribute("value", "{[PLC]Mixer}"))));
        document.Save(path);
        var project = await service.RegisterAsync(path);
        var report = service.GraphicsTagReferences(project.Id, new(project.CurrentSha256));
        Assert.Equal(2, report.Total);
        Assert.Equal("#1.Member", report.References[0].Reference);
        Assert.Equal("requires_parameter_context", report.References[0].Resolution);
        Assert.Null(report.References[0].TagPath);
        Assert.Equal("Mixer", report.References[1].TagPath);
        Assert.EndsWith("/@value", report.References[1].XPath);
    }

    [Fact]
    public async Task ExternalSourceChangesDoNotAlterManagedExtractionAndStaleHashesFail()
    {
        var (project, path, _) = await Fixture("{[PLC]Original}");
        File.WriteAllText(path, "External file changed after registration");
        Assert.Equal("Original", Assert.Single(service.GraphicsTagReferences(project.Id, new(project.CurrentSha256)).References).TagPath);
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.GraphicsTagReferences(project.Id, new("wrong"))).Code);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 201)]
    public async Task InvalidPaginationIsRejected(int offset, int limit)
    {
        var (project, _, _) = await Fixture("{[PLC]Value}");
        Assert.Equal("invalid_reference_query", Assert.Throws<OperationException>(() => service.GraphicsTagReferences(project.Id, new(project.CurrentSha256, Offset: offset, Limit: limit))).Code);
    }

    [Fact]
    public async Task OversizedExpressionsAreBounded()
    {
        var (project, _, _) = await Fixture("{" + new string('x', 16 * 1024) + "}");
        Assert.Equal("reference_limit", Assert.Throws<OperationException>(() => service.GraphicsTagReferences(project.Id, new(project.CurrentSha256))).Code);
    }

    public void Dispose() => service.Dispose();
}
