using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LogicDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LogicDiagnostics", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public LogicDiagnosticsTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private async Task<ProjectInfo> Project(string? extraRung = null)
    {
        var doc = XDocument.Parse("""
            <RSLogix5000Content><Controller Name="Generated"><Tags/><AddOnInstructionDefinitions><AddOnInstructionDefinition Name="GateType"><Parameters>
            <Parameter Name="EnableIn" DataType="BOOL" Usage="Input"/><Parameter Name="EnableOut" DataType="BOOL" Usage="Output"/>
            <Parameter Name="In" DataType="BOOL" Usage="Input" Required="true" AliasFor="LocalIn"/><Parameter Name="Out" DataType="BOOL" Usage="Output" Required="true"/>
            </Parameters><LocalTags><LocalTag Name="LocalIn" DataType="BOOL"/></LocalTags><Routines><Routine Name="Logic" Type="RLL"><RLLContent><Rung Number="0"><Text>XIC(In)OTE(Out);</Text></Rung></RLLContent></Routine></Routines></AddOnInstructionDefinition></AddOnInstructionDefinitions>
            <Programs><Program Name="Example"><Tags/><Routines><Routine Name="Main" Type="RLL"><RLLContent>
            <Rung Number="0"><Text>XIC(Enable)[XIC(Ready),XIO(Ready)]OTE(Output);</Text></Rung>
            <Rung Number="1"><Text>XIC(Enable)XIC(Ready)OTE(Output);</Text></Rung>
            <Rung Number="2"><Text>XIC(Uncaptured)OTE(Output);</Text></Rung>
            <Rung Number="3"><Text>TON(Timer,1000,0)XIC(Ready)OTE(Output);</Text></Rung>
            <Rung Number="4"><Text>MAGIC(Ready)XIC(Ready)OTE(Output);</Text></Rung>
            <Rung Number="5"><Text>MOV(1,Ready)XIC(Ready)OTE(Output);</Text></Rung>
            <Rung Number="6"><Text>XIC(Alias)OTE(Output);</Text></Rung>
            <Rung Number="7"><Text>GateType(Gate,Enable,Output);</Text></Rung>
            </RLLContent></Routine><Routine Name="Following" Type="RLL"><RLLContent><Rung Number="0"><Text>XIC(Output)OTE(Next);</Text></Rung></RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>
            """);
        var tags = doc.Descendants("Program").Single().Element("Tags")!;
        foreach (var (name, value) in new[] { ("Enable", "1"), ("Ready", "0"), ("Output", "0"), ("Next", "0"), ("Uncaptured", "1") })
            tags.Add(new XElement("Tag", new XAttribute("Name", name), new XAttribute("TagType", "Base"), new XAttribute("DataType", "BOOL"), new XElement("Data", new XAttribute("Format", "Decorated"), new XElement("DataValue", new XAttribute("DataType", "BOOL"), new XAttribute("Value", value)))));
        tags.Add(new XElement("Tag", new XAttribute("Name", "Alias"), new XAttribute("TagType", "Alias"), new XAttribute("AliasFor", "Enable")));
        tags.Add(new XElement("Tag", new XAttribute("Name", "Gate"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "GateType")));
        tags.Add(new XElement("Tag", new XAttribute("Name", "Timer"), new XAttribute("TagType", "Base"), new XAttribute("DataType", "TIMER")));
        if (extraRung is not null) doc.Descendants("Routine").Single(r => (string?)r.Attribute("Name") == "Main").Element("RLLContent")!.Add(new XElement("Rung", new XAttribute("Number", 8), new XElement("Text", extraRung)));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); doc.Save(path); return await service.RegisterAsync(path);
    }
    private Task<DiagnosticCaptureInfo> Capture(ProjectInfo p) => service.CaptureDiagnosticValuesAsync(new(p.Id, p.CurrentSha256, [new("enable", "Enable", "Example"), new("ready", "Ready", "Example")]));
    private RungEvaluationReport Evaluate(DiagnosticCaptureInfo capture, int index) => service.EvaluateCapturedRung(new(capture.Id, capture.Artifact.Sha256, "/RSLogix5000Content/Controller/Programs/Program/Routines/Routine[@Name='Main']", index));
    [Fact]
    public async Task TraceClassifiesEffectsFollowsAliasesAoiAliasesAndCrossRoutineDependencies()
    {
        var p = await Project(); var report = service.TraceLogic(p.Id, new(p.CurrentSha256, "Enable", "Example", Depth: 5));
        Assert.Contains("Program:Example.Next", report.Symbols); Assert.Contains(report.Usages, u => u.Role == "write" && u.Instruction == "OTE");
        Assert.Contains(report.Usages, u => u.Operand == "Alias" && u.AliasChain.Contains("Program:Example.Alias"));
        Assert.Contains(report.Dependencies, e => e.Kind == "aoi-input" && e.To == "Program:Example.Gate.LocalIn");
        Assert.Contains(report.Dependencies, e => e.Kind == "aoi-output" && e.From == "Program:Example.Gate.Out");
        Assert.Contains(report.Diagnostics, d => d.Contains("instruction_effect_unknown"));
    }
    [Fact]
    public async Task TraceReportsMissingDynamicOrAmbiguousOperandsAndDepthLimits()
    {
        var p = await Project("GateType(Gate,Enable)XIC(Ready[Index])OTE(Output);");
        var report = service.TraceLogic(p.Id, new(p.CurrentSha256, "Enable", "Example", Depth: 0));
        Assert.True(report.Limited); Assert.Contains(report.Diagnostics, d => d.Contains("aoi_binding")); Assert.Contains(report.Diagnostics, d => d.Contains("dynamic_address"));
        Assert.Throws<OperationException>(() => service.TraceLogic(p.Id, new("stale", "Enable", "Example")));
    }
    [Fact]
    public async Task BranchEvaluationUsesOrAndIdentifiesOnlyDecisiveBlockers()
    {
        var capture = await Capture(await Project()); var parallel = Evaluate(capture, 0); var series = Evaluate(capture, 1);
        Assert.Equal("true", parallel.Outputs.Single().Power); Assert.Empty(parallel.Outputs.Single().BlockingConditions);
        Assert.Equal("false", series.Outputs.Single().Power); var blocker = Assert.Single(series.Outputs.Single().BlockingConditions);
        Assert.Equal("Ready", series.Steps.Single(s => s.Id == blocker).Operands.Single());
    }
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task MissingValuesUnknownEffectsAndEarlierWritesNeverBecomeFalseCertainty(int index)
    {
        var report = Evaluate(await Capture(await Project()), index); Assert.Equal("unknown", report.Outputs.Single().Power); Assert.NotEmpty(report.Diagnostics);
    }
    [Fact]
    public async Task KnownFalseConditionStillBlocksAPathWithAnIndependentUnsupportedTimer()
    {
        var report = Evaluate(await Capture(await Project()), 3); Assert.Equal("false", report.Outputs.Single().Power); Assert.NotEmpty(report.Diagnostics);
    }
    [Fact]
    public async Task CaptureIsImmutableAndEvaluationUsesTheMatchingHistoricalRevision()
    {
        var p = await Project(); var capture = await Capture(p); var bytes = File.ReadAllBytes(service.Download(capture.Artifact.Id).Path);
        var draft = await service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("set_text", "/RSLogix5000Content/Controller/Programs/Program/Routines/Routine[@Name='Main']/RLLContent/Rung[2]/Text", "NOP();")]));
        await service.ApplyAsync(p.Id, draft.Id, new(p.CurrentSha256)); Assert.Equal("false", Evaluate(capture, 1).Outputs.Single().Power);
        Assert.Equal(bytes, File.ReadAllBytes(service.Download(capture.Artifact.Id).Path)); Assert.Single(service.DiagnosticCaptures());
        Assert.Throws<OperationException>(() => service.EvaluateCapturedRung(new(capture.Id, "wrong", "//Routine", 0)));
    }
    [Fact]
    public async Task MalformedRungsAreReportedAsUnknownAndNeverProduceInventedOutputs()
    {
        var report = Evaluate(await Capture(await Project("XIC(")), 8); Assert.Equal("unknown", report.Power); Assert.Empty(report.Outputs); Assert.NotEmpty(report.Diagnostics);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
