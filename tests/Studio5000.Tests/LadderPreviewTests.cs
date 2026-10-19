using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LadderPreviewTests
{
    [Fact]
    public void ParsesInstructionsArrayIndicesNestedExpressionsAndQuotedCommas()
    {
        var diagram = LadderPreview.Render("XIC(Flags[1,2])[CPT(Result,(A+B)*(C-D)),CustomAOI(Instance,'text[,],$'ok',Array[2])]OTE(Output);");
        Assert.Equal(4, diagram.Nodes.Length);
        Assert.Equal("Flags[1,2]", diagram.Nodes[0].Operands[0]);
        Assert.Contains(diagram.Nodes, n => n.Instruction == "CPT" && n.Operands[1] == "(A+B)*(C-D)");
        Assert.Contains(diagram.Nodes, n => n.Instruction == "CustomAOI" && n.Kind == "block" && n.Operands.Length == 3);
        Assert.True(diagram.Height > 200);
    }

    [Theory]
    [InlineData("[XIC(A),XIO(B)]OTE(C);")]
    [InlineData("BST XIC(A) NXB XIO(B) BND OTE(C);")]
    [InlineData("[XIC(A),[XIO(B),XIC(D)]XIC(E)]OTE(C);")]
    public void BranchesKeepEveryInstructionAndHaveConnectedVerticalTrunks(string text)
    {
        var diagram = LadderPreview.Render(text);
        Assert.Contains(diagram.Nodes, n => n.Kind == "coil" && n.Operands[0] == "C");
        Assert.True(diagram.Wires.Count(w => w.X1 == w.X2 && w.X1 > 15 && w.X1 < diagram.Width - 15) >= 2);
        Assert.All(diagram.Nodes, n => { Assert.InRange(n.X, 0, diagram.Width - n.Width); Assert.InRange(n.Y, 0, diagram.Height - n.Height); });
    }

    [Theory]
    [InlineData("")]
    [InlineData("XIC(A")]
    [InlineData("[XIC(A),XIO(B)OTE(C);")]
    [InlineData("BST XIC(A) NXB XIO(B);")]
    [InlineData("XIC(A);OTE(B);")]
    [InlineData("AOI(A,,B);")]
    [InlineData("CPT(A,Values[1));")]
    [InlineData("NXB XIC(A);")]
    public void MalformedRungsNeverProduceAPartialDiagram(string text)
    {
        Assert.Equal("invalid_ladder", Assert.Throws<OperationException>(() => LadderPreview.Render(text)).Code);
    }

    [Fact]
    public void SvgEncodesUntrustedOperandsAndHasNoExecutableContent()
    {
        var diagram = LadderPreview.Render("CustomAOI(Instance,'<script>alert(1)</script>');");
        var svg = XDocument.Parse(diagram.Svg);
        Assert.Equal("svg", svg.Root!.Name.LocalName);
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName is "script" or "foreignObject" or "image");
        Assert.DoesNotContain(svg.Descendants().Attributes(), a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) || a.Name.LocalName == "href");
        Assert.Contains("<script>", svg.Descendants().First(e => e.Name.LocalName == "g").Element(svg.Root.Name.Namespace + "title")!.Value);
    }

    [Fact]
    public void DeepBranchesAndLongRungsAreBounded()
    {
        Assert.Equal("ladder_limit", Assert.Throws<OperationException>(() => LadderPreview.Render(new string('[', 18) + "NOP()" + new string(']', 18))).Code);
        Assert.Equal("ladder_limit", Assert.Throws<OperationException>(() => LadderPreview.Render(new string('x', 65537))).Code);
    }

    [Fact]
    public void PagesOnlineEditVariantsAndKeepsMalformedRawTextWithDiagnostics()
    {
        var routine = XElement.Parse("<Routine Type='RLL'><RLLContent OnlineEditType='Original'><Rung Number='0'><Text>NOP();</Text></Rung></RLLContent><RLLContent OnlineEditType='Pending'><Rung Number='0'><Text>XIC(Broken</Text></Rung></RLLContent></Routine>");
        var project = new ProjectInfo("fixture", "Generated", "Fixture.L5X", "hash", "hash", 0, DateTimeOffset.UnixEpoch);
        var report = LadderPreview.Build(project, routine, new("hash", "//Routine", 1, 1));
        Assert.Equal(2, report.Total);
        var row = Assert.Single(report.Rungs);
        Assert.Equal("Pending", row.OnlineEditType); Assert.Equal("invalid", row.Status);
        Assert.Equal("XIC(Broken", row.RawText); Assert.Null(row.Diagram); Assert.NotEmpty(row.Diagnostics);
    }

    [Fact]
    public async Task ServiceUsesTheManagedSnapshotAndRejectsStaleHashesAndNonLadderTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "RockwellLadderTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "fixture.l5x");
        File.WriteAllText(source, "<RSLogix5000Content><Controller><Programs><Program Name='Example'><Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Text>XIC(A)OTE(B);</Text></Rung></RLLContent></Routine><Routine Name='Other' Type='ST'/></Routines></Program></Programs></Controller></RSLogix5000Content>");
        using var service = new ProjectService(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
        var project = await service.RegisterAsync(source); var before = File.ReadAllBytes(source);
        var report = service.PreviewLadder(project.Id, new(project.CurrentSha256, "//Routine[@Name='Main']"));
        Assert.Equal(2, Assert.Single(report.Rungs).Diagram!.Nodes.Length); Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.PreviewLadder(project.Id, new("stale", "//Routine"))).Code);
        Assert.Equal("not_ladder", Assert.Throws<OperationException>(() => service.PreviewLadder(project.Id, new(project.CurrentSha256, "//Routine[@Name='Other']"))).Code);
    }
}
