using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ProjectComparisonTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellCompareTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private const string Xml = """
        <RSLogix5000Content ExportDate="one"><Controller Name="Example" LastModifiedDate="one">
        <DataTypes><DataType Name="Pair"><Members><Member Name="A" DataType="DINT"/><Member Name="B" DataType="DINT"/></Members></DataType></DataTypes>
        <Tags><Tag Name="Count" DataType="DINT"><Data><DataValue Value="1"/></Data><Description>Count values</Description></Tag></Tags>
        <AddOnInstructionDefinitions><AddOnInstructionDefinition Name="ExampleAOI"><Parameters><Parameter Name="A"/><Parameter Name="B"/></Parameters></AddOnInstructionDefinition></AddOnInstructionDefinitions>
        <Programs><Program Name="ExampleProgram"><Tags><Tag Name="Count" DataType="DINT"/></Tags><Routines><Routine Name="MainRoutine" Type="RLL"><RLLContent>
        <Rung Number="0" Type="N"><Text>XIC(A)OTE(B);</Text></Rung><Rung Number="1" Type="N"><Text>NOP();</Text></Rung>
        </RLLContent></Routine></Routines></Program></Programs><Tasks/></Controller></RSLogix5000Content>
        """;
    public ProjectComparisonTests()
    {
        Directory.CreateDirectory(root);
        service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private async Task<ProjectInfo> Register(string xml)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); File.WriteAllText(path, xml);
        return await service.RegisterAsync(path);
    }
    private static ProjectCompareRequest Request(ProjectInfo a, ProjectInfo b) => new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256);

    [Fact]
    public async Task XmlFormattingAttributeOrderAndExportTimestampsDoNotHideScalarChanges()
    {
        var a = await Register(Xml);
        var reordered = Xml.Replace("ExportDate=\"one\"", "ExportDate=\"two\"").Replace("LastModifiedDate=\"one\"", "LastModifiedDate=\"two\"")
            .Replace("Name=\"Count\" DataType=\"DINT\"", "DataType=\"DINT\" Name=\"Count\"");
        var b = await Register(XDocument.Parse(reordered).ToString(SaveOptions.DisableFormatting));
        Assert.Equal(0, service.CompareProjects(Request(a, b)).TotalChanges);
        var c = await Register(reordered.Replace("Count values", " Count values "));
        Assert.Equal("modified", Assert.Single(service.CompareProjects(Request(a, c)).Differences).Change);
    }

    [Fact]
    public async Task InsertingARungDoesNotMarkFollowingRenumberedRungsModified()
    {
        var doc = XDocument.Parse(Xml); var content = doc.Descendants("RLLContent").Single();
        foreach (var rung in content.Elements()) rung.SetAttributeValue("Number", int.Parse((string)rung.Attribute("Number")!) + 1);
        content.AddFirst(new XElement("Rung", new XAttribute("Number", "0"), new XAttribute("Type", "N"), new XElement("Text", "XIC(New)OTE(Output);")));
        var a = await Register(Xml); var b = await Register(doc.ToString());
        var report = service.CompareProjects(Request(a, b));
        Assert.Equal(1, report.TotalChanges); Assert.Equal(1, report.Added); Assert.Equal("Rung", Assert.Single(report.Differences).Kind);
        Assert.Contains("New", report.Differences[0].RightXml);
        Assert.True(service.CompareProjects(Request(a, b) with { IncludeRungNumbers = true }).TotalChanges > 1);
    }

    [Fact]
    public async Task SameRungCountCanContainARealLogicChange()
    {
        var a = await Register(Xml); var b = await Register(Xml.Replace("XIC(A)", "XIO(A)"));
        var difference = Assert.Single(service.CompareProjects(Request(a, b)).Differences);
        Assert.Equal("modified", difference.Change); Assert.Equal("Rung", difference.Kind);
        Assert.Contains("XIO(A)", difference.RightXml);
    }

    [Fact]
    public async Task RepeatedIdenticalRungsAreAlignedAsOccurrences()
    {
        var doc = XDocument.Parse(Xml); var content = doc.Descendants("RLLContent").Single();
        content.AddFirst(new XElement(content.Elements().First()));
        var a = await Register(doc.ToString()); var b = await Register(Xml);
        var report = service.CompareProjects(Request(a, b));
        Assert.Equal(1, report.Removed); Assert.Equal(0, report.Modified);
    }

    [Theory]
    [InlineData("Members", "DataType")]
    [InlineData("Parameters", "Parameters")]
    public async Task LayoutSensitiveMemberAndParameterOrderRemainsSignificant(string container, string kind)
    {
        var doc = XDocument.Parse(Xml); var node = doc.Descendants(container).Single();
        var children = node.Elements().Reverse().Select(e => new XElement(e)).ToArray(); node.ReplaceNodes(children);
        var a = await Register(Xml); var b = await Register(doc.ToString());
        var difference = Assert.Single(service.CompareProjects(Request(a, b)).Differences);
        Assert.Equal(kind, difference.Kind); Assert.Equal("modified", difference.Change);
    }

    [Fact]
    public async Task StoredTagValuesUnknownComponentsAndScopesAreCompared()
    {
        var a = await Register(Xml); var b = await Register(Xml.Replace("Value=\"1\"", "Value=\"2\"").Replace("<Tasks/>", "<Tasks/><OpaqueSetting Value='changed'/>"));
        var report = service.CompareProjects(Request(a, b));
        Assert.Equal(2, report.TotalChanges);
        Assert.Contains(report.Differences, d => d.Kind == "Tag" && d.Change == "modified");
        Assert.Contains(report.Differences, d => d.Kind == "OpaqueSetting" && d.Change == "added");
        Assert.Equal(1, service.CompareProjects(Request(a, b) with { Query = "Opaque", Limit = 1 }).FilteredChanges);
    }

    [Fact]
    public async Task CaseOnlyNameChangesAreReportedAndParentsAreNotDoubleCounted()
    {
        var a = await Register(Xml); var b = await Register(Xml.Replace("Name=\"Count\"", "Name=\"count\""));
        var report = service.CompareProjects(Request(a, b));
        Assert.Equal(2, report.Modified); Assert.Equal(0, report.Added); Assert.Equal(0, report.Removed);
        Assert.All(report.Differences, d => Assert.Equal("Tag", d.Kind));
    }

    [Fact]
    public async Task ReadOnlyComparisonPreservesInputsAndRejectsStaleHashes()
    {
        var a = await Register(Xml); var b = await Register(Xml.Replace("Value=\"1\"", "Value=\"2\""));
        var before = Directory.GetFiles(root, "*.l5x").ToDictionary(p => p, p => ProjectService.Hash(File.ReadAllBytes(p)));
        _ = service.CompareProjects(Request(a, b));
        Assert.Equal(2, service.Projects().Count()); Assert.Equal(a, service.Get(a.Id)); Assert.Equal(b, service.Get(b.Id));
        Assert.All(before, p => Assert.Equal(p.Value, ProjectService.Hash(File.ReadAllBytes(p.Key))));
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.CompareProjects(Request(a, b) with { RightSha256 = "stale" })).Code);
    }

    [Fact]
    public async Task LargeUnrelatedRungSequencesFailWithinABoundedAlignmentBudget()
    {
        string Rungs(string operand)
        {
            var doc = XDocument.Parse(Xml);
            doc.Descendants("RLLContent").Single().ReplaceNodes(Enumerable.Range(0, 1100).Select(i =>
                new XElement("Rung", new XAttribute("Number", i), new XAttribute("Type", "N"), new XElement("Text", $"XIC({operand}{i})OTE(Out);"))));
            return doc.ToString();
        }
        var a = await Register(Rungs("A")); var b = await Register(Rungs("B"));
        Assert.Equal("comparison_limit", Assert.Throws<OperationException>(() => service.CompareProjects(Request(a, b))).Code);
    }
    public void Dispose() => service.Dispose();
}
