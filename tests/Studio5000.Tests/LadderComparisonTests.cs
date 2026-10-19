using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LadderComparisonTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LadderCompareTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private const string Xml = "<RSLogix5000Content><Controller Name='Demo'><Programs><Program Name='Example'><Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Text>XIC(Enable)OTE(Output);</Text></Rung><Rung Number='1'><Text>NOP();</Text></Rung></RLLContent></Routine><Routine Name='Text' Type='ST'><STContent><Line Number='0'><Text>A := 1;</Text></Line></STContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
    public LadderComparisonTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private async Task<ProjectInfo> Project(Action<XDocument>? edit = null)
    {
        var doc = XDocument.Parse(Xml); edit?.Invoke(doc); var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); doc.Save(file); return await service.RegisterAsync(file);
    }
    private static ProjectCompareRequest Request(ProjectInfo a, ProjectInfo b) => new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256);
    private LadderCompareRequest Ladder(ProjectInfo a, ProjectInfo b)
    {
        var pair = service.CompareRoutines(Request(a, b)).Routines.Single(r => r.Name == "Main");
        return new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, pair.LeftXPath, pair.RightXPath, Limit: 20);
    }
    [Fact]
    public async Task RoutineBrowserIncludesUnchangedChangedOneSidedAndSeparateAoiScopes()
    {
        var a = await Project(); var b = await Project(doc =>
        {
            doc.Descendants("Routine").First().Descendants("Text").First().Value = "XIO(Enable)OTE(Output);";
            doc.Descendants("Routine").Single(r => (string?)r.Attribute("Name") == "Text").Remove();
            doc.Root!.Element("Controller")!.Add(new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "Example"),
                new XElement("Routines", new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"))))));
        });
        var report = service.CompareRoutines(Request(a, b)); Assert.Equal(3, report.Total);
        Assert.Contains(report.Routines, r => r.Owner == "AOI:Example" && r.Change == "added");
        Assert.Contains(report.Routines, r => r.Name == "Text" && r.Change == "removed" && r.RightXPath is null);
        Assert.Contains(report.Routines, r => r.Owner == "Program:Example" && r.Change == "modified" && r.LeftRungs == 2);
        Assert.Single(service.CompareRoutines(Request(a, b) with { Query = "AOI:", Limit = 1 }).Routines);
    }
    [Fact]
    public async Task InsertionsKeepFollowingRungsPairedAndExposeAnExplicitEmptySide()
    {
        var a = await Project(); var b = await Project(doc =>
        {
            var content = doc.Descendants("RLLContent").Single(); foreach (var r in content.Elements()) r.SetAttributeValue("Number", int.Parse((string)r.Attribute("Number")!) + 1);
            content.AddFirst(new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "XIC(New)OTE(Out);")));
        });
        var req = Ladder(a, b); var result = service.CompareLadder(req); Assert.Equal(3, result.Total);
        Assert.Equal("added", result.Rungs[0].Change); Assert.Null(result.Rungs[0].Left); Assert.NotNull(result.Rungs[0].Right!.Diagram);
        Assert.Equal("unchanged", result.Rungs[1].Change); Assert.Equal(0, result.Rungs[1].Left!.Index); Assert.Equal(1, result.Rungs[1].Right!.Index);
        var page = service.CompareLadder(req with { Offset = 1, Limit = 1 }); Assert.Equal(1, page.Rungs.Single().Position);
        var reverse = service.CompareLadder(new(b.Id, b.CurrentSha256, a.Id, a.CurrentSha256, req.RightRoutineXPath, req.LeftRoutineXPath)); Assert.Equal("removed", reverse.Rungs.Single().Change); Assert.Null(reverse.Rungs.Single().Right);
    }
    [Fact]
    public async Task ModifiedRungsRetainBothDiagramsAndInvalidLogicRetainsSourceAndDiagnostics()
    {
        var a = await Project(); var b = await Project(d => d.Descendants("Text").First().Value = "XIC(");
        var result = service.CompareLadder(Ladder(a, b)); var modified = result.Rungs[0];
        Assert.Equal("modified", modified.Change); Assert.NotNull(modified.Left!.Diagram); Assert.Null(modified.Right!.Diagram); Assert.Equal("XIC(", modified.Right.RawText); Assert.NotEmpty(modified.Right.Diagnostics);
    }
    [Fact]
    public async Task WholeAddedAndEmptyRoutinesAreBrowsable()
    {
        var a = await Project(d => d.Descendants("Routine").First().Remove()); var b = await Project();
        var req = Ladder(a, b); var result = service.CompareLadder(req); Assert.All(result.Rungs, r => { Assert.Null(r.Left); Assert.Equal("added", r.Change); });
        var empty = await Project(d => d.Descendants("RLLContent").Single().RemoveNodes());
        Assert.Empty(service.CompareLadder(Ladder(empty, empty)).Rungs);
    }
    [Fact]
    public async Task HashTypeSelectorAndPageGuardsRejectBeforeRenderingAndPreserveSources()
    {
        var a = await Project(); var b = await Project(); var req = Ladder(a, b);
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.CompareLadder(req with { RightSha256 = "stale" })).Code);
        Assert.Equal("not_ladder", Assert.Throws<OperationException>(() => service.CompareLadder(req with { LeftRoutineXPath = "//Routine[@Name='Text']" })).Code);
        Assert.Throws<OperationException>(() => service.CompareLadder(req with { LeftRoutineXPath = "//Routine" }));
        Assert.Throws<OperationException>(() => service.CompareLadder(req with { Limit = 21 }));
        Assert.Throws<OperationException>(() => service.CompareLadder(req with { LeftRoutineXPath = null, RightRoutineXPath = null }));
        _ = service.CompareLadder(req); Assert.Equal(a.SourceSha256, ProjectService.Hash(File.ReadAllBytes(a.SourcePath))); Assert.Equal(b, service.Get(b.Id));
    }
    [Fact]
    public async Task LocateLateModifiedRungUsesAlignedOccurrencesAndFilteredOffset()
    {
        void Many(XDocument d) => d.Descendants("RLLContent").Single().ReplaceNodes(Enumerable.Range(0, 12).Select(i => new XElement("Rung", new XAttribute("Number", i), new XElement("Text", $"MOV({i},Output);"))));
        var a = await Project(Many); var b = await Project(d =>
        {
            Many(d); var rungs = d.Descendants("Rung").ToArray(); rungs[9].Element("Text")!.Value = "MOV(99,Output);";
            rungs[5].AddBeforeSelf(new XElement("Rung", new XAttribute("Number", "5"), new XElement("Text", "XIC(New)NOP();")));
        });
        var request = Request(a, b); var diff = service.CompareProjects(request).Differences.Single(r => r.Kind == "Rung" && r.Change == "modified");
        var located = service.LocateComparedLadder(new(request, diff.LeftXPath, diff.RightXPath)); var rung = located.Ladder.Rungs.Single();
        Assert.Equal(10, located.Ladder.Offset); Assert.Equal(9, rung.Left!.Index); Assert.Equal(10, rung.Right!.Index); Assert.Equal("modified", rung.Change);
        var filtered = service.LocateComparedLadder(new(request, diff.LeftXPath, diff.RightXPath, true));
        Assert.Equal(1, filtered.Ladder.Offset); Assert.Equal(10, filtered.Ladder.Rungs.Single().Position); Assert.Equal(2, filtered.Ladder.Total);
        Assert.Equal(a.SourceSha256, ProjectService.Hash(File.ReadAllBytes(a.SourcePath))); Assert.Equal(b, service.Get(b.Id));
    }
    [Fact]
    public async Task LocateOneSidedRungInfersItsExistingRoutineCounterpart()
    {
        var a = await Project(); var b = await Project(d => d.Descendants("RLLContent").Single().AddFirst(new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "XIO(New)NOP();"))));
        var request = Request(a, b); var diff = service.CompareProjects(request).Differences.Single();
        var located = service.LocateComparedLadder(new(request, diff.LeftXPath, diff.RightXPath));
        Assert.NotNull(located.Routine.LeftXPath); Assert.NotNull(located.Routine.RightXPath); Assert.Null(located.Ladder.Rungs.Single().Left); Assert.Equal("added", located.Ladder.Rungs.Single().Change);
        request = Request(b, a); diff = service.CompareProjects(request).Differences.Single(); located = service.LocateComparedLadder(new(request, diff.LeftXPath, diff.RightXPath));
        Assert.Equal("removed", located.Ladder.Rungs.Single().Change); Assert.Null(located.Ladder.Rungs.Single().Right);
    }
    [Fact]
    public async Task LocateWholeAddedAndEmptyRoutineDoesNotRequireARung()
    {
        var a = await Project(d => d.Descendants("Routine").First().Remove()); var b = await Project();
        var request = Request(a, b); var diff = service.CompareProjects(request).Differences.Single(); var located = service.LocateComparedLadder(new(request, null, diff.RightXPath));
        Assert.Null(located.Routine.LeftXPath); Assert.Equal("added", located.Ladder.Rungs.Single().Change);
        var empty = await Project(d => d.Descendants("RLLContent").Single().RemoveNodes());
        located = service.LocateComparedLadder(new(Request(empty, empty), "//Routine[@Name='Main']", "//Routine[@Name='Main']")); Assert.Empty(located.Ladder.Rungs); Assert.Equal(0, located.Ladder.Total);
    }
    [Fact]
    public async Task LocateDuplicateNumberedRungsUsesTheExactDeletedOccurrence()
    {
        var a = await Project(d => d.Descendants("RLLContent").Single().ReplaceNodes(Enumerable.Range(0, 3).Select(_ => new XElement("Rung", new XAttribute("Number", 0), new XElement("Text", "NOP();")))));
        var b = await Project(d => d.Descendants("RLLContent").Single().ReplaceNodes(Enumerable.Range(0, 2).Select(_ => new XElement("Rung", new XAttribute("Number", 0), new XElement("Text", "NOP();")))));
        var request = Request(a, b); var diff = service.CompareProjects(request).Differences.Single();
        var result = service.LocateComparedLadder(new(request, diff.LeftXPath, diff.RightXPath));
        Assert.Equal(2, result.Ladder.Offset); Assert.Equal(2, result.Ladder.Rungs.Single().Left!.Index); Assert.Null(result.Ladder.Rungs.Single().Right);
    }
    [Fact]
    public async Task LocateRejectsStaleNonLadderAmbiguousAndMismatchedRoutineAnchors()
    {
        var a = await Project(); var b = await Project(d => d.Descendants("Routines").Single().Add(new XElement("Routine", new XAttribute("Name", "Other"), new XAttribute("Type", "RLL"))));
        var request = Request(a, b);
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.LocateComparedLadder(new(request with { RightSha256 = "old" }, "//Routine", null))).Code);
        foreach (var xpath in new[] { "//Controller", "//Routine", "//Routine[@Name='Text']", "//Routine[@Name='Main']/RLLContent/Rung/Text", "//*[" })
            Assert.Throws<OperationException>(() => service.LocateComparedLadder(new(request, xpath, null)));
        Assert.Equal("comparison_navigation_target", Assert.Throws<OperationException>(() => service.LocateComparedLadder(new(request, "//Routine[@Name='Main']", "//Routine[@Name='Other']"))).Code);
        Assert.Throws<OperationException>(() => service.LocateComparedLadder(new(request, null, null)));
    }
    [Fact]
    public async Task LocateRejectsUnpairedOccurrencesAndRungsHiddenByTheFilter()
    {
        var a = await Project(); var b = await Project(); var request = Request(a, b);
        Assert.Equal("comparison_navigation_target", Assert.Throws<OperationException>(() => service.LocateComparedLadder(new(request, "//Rung[1]", "//Rung[2]"))).Code);
        Assert.Equal("comparison_navigation_target", Assert.Throws<OperationException>(() => service.LocateComparedLadder(new(request, "//Rung[1]", "//Rung[1]", true))).Code);
    }
    [Fact]
    public async Task LocateDoesNotTreatARungOutsideRllContentAsARealSourceOccurrence()
    {
        var a = await Project(d => d.Descendants("Routine").First().Add(new XElement("Opaque", new XElement("Rung", new XElement("Text", "NOP();"))))); var b = await Project();
        Assert.Equal("comparison_navigation_target", Assert.Throws<OperationException>(() => service.LocateComparedLadder(new(Request(a, b), "//Opaque/Rung", null))).Code);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
