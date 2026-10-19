using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Extensions.Rockwell;
using Xunit;

namespace Studio5000.Tests;

public sealed class TagReferenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellTagReferences", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public TagReferenceTests()
    {
        Directory.CreateDirectory(root);
        service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private static XDocument Document(string[] rungs, string[]? lines = null)
    {
        var document = XDocument.Parse("""
            <RSLogix5000Content><Controller Name="Generated"><Tags><Tag Name="Value" DataType="DINT"/><Tag Name="Index" DataType="DINT"/><Tag Name="Items" DataType="DINT" Dimensions="10"/></Tags>
            <Programs><Program Name="First"><Tags><Tag Name="Value" DataType="DINT"/></Tags><Routines/></Program>
            <Program Name="Second"><Tags/><Routines><Routine Name="Other" Type="RLL"><RLLContent><Rung Number="0"><Text>ADD(Value,1,Value);</Text></Rung></RLLContent></Routine></Routines></Program></Programs>
            </Controller></RSLogix5000Content>
            """);
        var routines = document.Descendants("Program").First().Element("Routines")!;
        routines.Add(new XElement("Routine", new XAttribute("Name", "MainRoutine"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", rungs.Select((r, i) => new XElement("Rung", new XAttribute("Number", i), new XElement("Text", new XCData(r)))))));
        routines.Add(new XElement("Routine", new XAttribute("Name", "Calculate"), new XAttribute("Type", "ST"),
            new XElement("STContent", (lines ?? []).Select((l, i) => new XElement("Line", new XAttribute("Number", i), new XCData(l))))));
        return document;
    }
    private async Task<ProjectInfo> Register(XDocument document)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); document.Save(path); return await service.RegisterAsync(path);
    }
    private TagReferenceReport Find(ProjectInfo p, string tag = "Value", string? program = "First") => service.FindTagReferences(p.Id, new(p.CurrentSha256, tag, program));

    [Fact]
    public async Task LocalShadowingAndQualifiedReferencesHaveExactSourcePositions()
    {
        var doc = Document(["ADD(Value,1,Value); XIC(Program:First.Value.0)OTE(ValueExtra);"]);
        var p = await Register(doc); var local = Find(p, "vAlUe", "fIrSt"); var global = Find(p, program: null);
        Assert.Equal("Value", local.TagName); Assert.Equal("First", local.ProgramName); Assert.Equal(3, local.Total); Assert.Equal(2, global.Total);
        Assert.All(local.References, r =>
        {
            var text = (string)doc.XPathEvaluate("string(" + r.SourceXPath + ")"); Assert.Equal(r.Operand, text.Substring(r.Start, r.Length));
            Assert.Equal("First", r.ProgramName); Assert.Equal("candidate", r.Status);
        });
        Assert.All(global.References, r => Assert.Equal("Second", r.ProgramName));
    }
    [Theory]
    [InlineData("Value := Value + 1;", 2)]
    [InlineData("Value:=Value; // Value", 2)]
    [InlineData("(* Value *) Value := 1; /* Value */", 1)]
    [InlineData("Value := 16#FACE;", 1)]
    [InlineData("Value := 'Value';", 1)]
    [InlineData("Value := \"Value\";", 1)]
    [InlineData("Value := 'escaped $' Value';", 1)]
    [InlineData("Value := 'doubled '' Value';", 1)]
    [InlineData("Value(Value);", 1)]
    [InlineData("Other.Value := 0;", 0)]
    [InlineData("ValueExtra := 0;", 0)]
    [InlineData("Local:1:I.Value := 0;", 0)]
    public async Task StructuredTextSkipsLiteralsCommentsCallsAndMemberNameFalseMatches(string line, int count)
    {
        var p = await Register(Document([], [line])); var result = Find(p); Assert.Equal(count, result.Total);
        Assert.All(result.References, r => Assert.Equal("ST", r.RoutineType));
    }
    [Fact]
    public async Task BlockCommentsSpanXmlLinesWithoutLosingOffsetsAndNumberedSelectors()
    {
        var doc = Document([], ["(* Value", "nested /* Value */", "*) Value := 1;", "/* Value", "Value */ Value := 2;"]);
        var p = await Register(doc); var result = Find(p); Assert.Equal(2, result.Total);
        Assert.Equal(new[] { "2", "4" }, result.References.Select(r => r.Number));
        Assert.All(result.References, r => Assert.Equal("Value", ((string)doc.XPathEvaluate("string(" + r.SourceXPath + ")")).Substring(r.Start, r.Length)));
    }
    [Fact]
    public async Task IndirectArrayBasesAndIndexExpressionsAreSeparateOccurrences()
    {
        var p = await Register(Document(["MOV(Items[Index + Items[0]],Items[1,2]);"]));
        var items = Find(p, "Items", null); Assert.Equal(3, items.Total);
        Assert.Equal("dynamic_address_candidate", items.References[0].Status); Assert.Equal("index_expression", items.References[1].Role);
        Assert.Equal("Items[1,2]", items.References[2].Operand);
        var index = Assert.Single(Find(p, "Index", null).References); Assert.Equal("index_expression", index.Role); Assert.Equal("Index", index.Operand);
    }
    [Fact]
    public async Task AliasDeclarationsAreDirectCandidatesWithoutTransitiveExpansion()
    {
        var doc = Document(["XIC(Alias)OTE(AliasTwo);"]);
        var tags = doc.Descendants("Program").First().Element("Tags")!;
        tags.Add(new XElement("Tag", new XAttribute("Name", "Alias"), new XAttribute("TagType", "Alias"), new XAttribute("AliasFor", "Value.1")),
            new XElement("Tag", new XAttribute("Name", "AliasTwo"), new XAttribute("TagType", "Alias"), new XAttribute("AliasFor", "Alias")));
        var p = await Register(doc); var result = Find(p); var alias = Assert.Single(result.References);
        Assert.Equal("alias", alias.Kind); Assert.Equal("Alias", alias.OwnerName); Assert.EndsWith("/@AliasFor", alias.SourceXPath);
        Assert.Equal("Value.1", doc.XPathEvaluate("string(" + alias.SourceXPath + ")"));
        Assert.Equal(2, Find(p, "Alias").Total);
    }
    [Fact]
    public async Task UnsupportedScopesAndIncompleteSyntaxStayVisible()
    {
        var doc = Document(["MOV(Items[Index,Value);"]);
        doc.Descendants("Routines").First().Add(new XElement("Routine", new XAttribute("Name", "Chart"), new XAttribute("Type", "SFC")));
        doc.Root!.Element("Controller")!.Add(new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "Example"), new XElement("Routines",
            new XElement("Routine", new XAttribute("Name", "Logic"), new XAttribute("Type", "ST"), new XElement("STContent", new XElement("Line", "Value:=1;")))))));
        var p = await Register(doc); var result = Find(p, "Items", null);
        Assert.Equal("syntax_unresolved", Assert.Single(result.References).Status); Assert.Equal(3, result.SkippedSources);
        Assert.Contains(result.Diagnostics, s => s.StartsWith("unterminated_array_selector:"));
        Assert.Contains(result.Diagnostics, s => s.StartsWith("unsupported_routine_type:")); Assert.Contains(result.Diagnostics, s => s.StartsWith("aoi_scope_not_scanned:"));
    }
    [Theory]
    [InlineData("Value:=1; (* Value", "unterminated_comment")]
    [InlineData("Value:=1; 'Value", "unterminated_string")]
    public async Task UnterminatedRegionsDoNotTurnTrailingTextIntoCandidates(string line, string diagnostic)
    {
        var p = await Register(Document([], [line])); var report = Find(p); Assert.Single(report.References); Assert.Contains(report.Diagnostics, s => s.StartsWith(diagnostic));
    }
    [Fact]
    public async Task FilteringPagingSourceCopiesAndStaleHashesRemainBound()
    {
        var p = await Register(Document(["MOV(Value,Value);", "MOV(Value,Value);"])); var before = ProjectService.Hash(File.ReadAllBytes(p.SourcePath));
        var page = service.FindTagReferences(p.Id, new(p.CurrentSha256, "Value", "First", "mainroutine", 1, 1));
        Assert.Equal(4, page.Total); Assert.Single(page.References); Assert.Equal(p.CurrentSha256, page.Project.CurrentSha256);
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.FindTagReferences(p.Id, new("stale", "Value"))).Code);
        Assert.Equal("reference_tag_missing", Assert.Throws<OperationException>(() => Find(p, "Absent")).Code);
        Assert.Equal("invalid_tag_reference_query", Assert.Throws<OperationException>(() => service.FindTagReferences(p.Id, new(p.CurrentSha256, "Value", Limit: 201))).Code);
        Assert.Equal(before, ProjectService.Hash(File.ReadAllBytes(p.SourcePath))); Assert.Equal(p, service.Get(p.Id));
        File.WriteAllText(p.SourcePath, "changed externally"); Assert.Equal(4, Find(p).Total);
    }
    [Fact]
    public async Task NumericAndTypedLiteralsDoNotReferenceMatchingNames()
    {
        var doc = Document([], ["Value:=16#FACE; Value:=DINT#1;"]);
        doc.Descendants("Program").First().Element("Tags")!.Add(new XElement("Tag", new XAttribute("Name", "FACE")), new XElement("Tag", new XAttribute("Name", "DINT")));
        var p = await Register(doc); Assert.Empty(Find(p, "FACE").References); Assert.Empty(Find(p, "DINT").References);
    }
    [Fact]
    public async Task RepeatedOccurrencesKeepBoundsAndLongSourceSnippetsTruncate()
    {
        var p = await Register(Document([new string(' ', 1000) + "ADD(Value,Value,Value);" + new string(' ', 1000)]));
        var report = Find(p); Assert.Equal(3, report.Total); Assert.All(report.References, r => { Assert.True(r.SnippetTruncated); Assert.True(r.Snippet.Length <= 512); Assert.True(r.SnippetStart > 0); });
        var tooLarge = await Register(Document([new string(' ', 65537)])); Assert.Equal("tag_reference_limit", Assert.Throws<OperationException>(() => Find(tooLarge)).Code);
        var many = await Register(Document([string.Concat(Enumerable.Repeat("XIC(Value)", 1001))])); Assert.Equal(1001, Find(many).Total);
    }
    [Fact]
    public void ExtensionUsesAReadSchemaWithTheSharedRequestContract()
    {
        var operation = new LogixExtension().Describe().Single(o => o.Name == "studio_tag_references"); Assert.Equal("read", operation.Effect);
        ExtensionSchema.Validate(operation.InputSchema, JsonSerializer.SerializeToElement(new { projectId = "id", request = new TagReferenceRequest("hash", "Value", "First") }, ProjectService.Json));
    }
    [Fact]
    public async Task DiagnosticsAndCandidateBudgetsAreBoundedEvenWhenNothingMatches()
    {
        var p = await Register(Document(Enumerable.Repeat("Local:1:I.Data;", 60).ToArray())); var report = Find(p);
        Assert.True(report.DiagnosticsTruncated); Assert.Equal(50, report.Diagnostics.Length); Assert.Empty(report.References);
        var tooManyTokens = await Register(Document([], [string.Concat(Enumerable.Repeat("Other;", 100001))]));
        Assert.Equal("tag_reference_limit", Assert.Throws<OperationException>(() => Find(tooManyTokens)).Code);
        var tooManyMatches = await Register(Document([], [string.Concat(Enumerable.Repeat("Value;", 10001))]));
        Assert.Equal("tag_reference_limit", Assert.Throws<OperationException>(() => Find(tooManyMatches)).Code);
    }
    [Fact]
    public async Task DuplicateRungNumbersRetainDistinctSelectorsAndLadderOffsets()
    {
        var doc = Document(["MOV(Value,Value);", "MOV(Value,Value);"]);
        foreach (var rung in doc.Descendants("Program").First().Descendants("Rung")) rung.SetAttributeValue("Number", "0");
        var p = await Register(doc); var report = Find(p);
        Assert.Equal(new int?[] { 0, 0, 1, 1 }, report.References.Select(r => r.RungIndex));
        Assert.Equal(2, report.References.Select(r => r.SourceXPath).Distinct().Count());
        foreach (var reference in report.References)
        {
            var preview = service.PreviewLadder(p.Id, new(p.CurrentSha256, reference.RoutineXPath!, reference.RungIndex!.Value, 1));
            Assert.Equal(reference.RungIndex, Assert.Single(preview.Rungs).Index);
        }
    }
    [Fact]
    public async Task EncodedPageLimitCanBeRecoveredByRequestingFewerOccurrences()
    {
        var p = await Register(Document(Enumerable.Repeat("MOV(Value[" + new string('<', 3500) + "],0);", 200).ToArray()));
        Assert.Equal("tag_reference_page_too_large", Assert.Throws<OperationException>(() => service.FindTagReferences(p.Id, new(p.CurrentSha256, "Value", "First", Limit: 200))).Code);
        var page = service.FindTagReferences(p.Id, new(p.CurrentSha256, "Value", "First", Limit: 1));
        Assert.Equal(200, page.Total); Assert.Single(page.References);
    }
    public void Dispose() => service.Dispose();
}
