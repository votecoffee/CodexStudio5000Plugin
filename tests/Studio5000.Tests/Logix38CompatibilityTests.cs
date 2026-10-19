using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class Logix38CompatibilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellLogix38", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private readonly string source;
    public Logix38CompatibilityTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "Generated38.L5X");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"), source);
        service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private static void AssertV38(XDocument document)
    {
        Assert.Equal("38.01", (string?)document.Root!.Attribute("SoftwareRevision"));
        Assert.Equal("38", (string?)document.Root.Element("Controller")!.Attribute("MajorRev"));
        Assert.Equal("true", (string?)document.Root.Element("Controller")!.Attribute("AutoDiagsEnabled"));
    }
    [Fact]
    public async Task V38EditsAndExportsPreserveRevisionMetadataAndOriginalInput()
    {
        var document = XDocument.Load(source); AssertV38(document);
        var program = document.Descendants("Program").Single();
        program.Descendants("Routine").Single().Add(new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "NOP();"))));
        var tag = program.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Record"); tag.AddFirst(new XElement("Description", "Generated v38 baseline"));
        document.Save(source); var before = File.ReadAllBytes(source);
        var baseline = await service.RegisterAsync(source); var project = await service.RegisterAsync(source);
        var draft = await service.PreviewAsync(project.Id, new(project.CurrentSha256,
            [new("set_text", "//Tag[@Name='Record']/Description", "Generated v38 edited copy"),
             new("set_text", "//Routine[@Name='main']/RLLContent/Rung/Text", "XIC(Enabled)ADD(Record.Inner.Count,1,Record.Count);")]));
        var updated = await service.ApplyAsync(project.Id, draft.Id, new(project.CurrentSha256));
        var artifact = await service.ExportAsync(updated.Id, new(updated.CurrentSha256));
        var bytes = await File.ReadAllBytesAsync(service.Download(artifact.Id).Path); AssertV38(XmlProjects.Parse(bytes));
        Assert.Equal(updated.CurrentSha256, ProjectService.Hash(bytes)); Assert.Equal(before, File.ReadAllBytes(source));
        var differences = service.CompareProjects(new(baseline.Id, baseline.CurrentSha256, updated.Id, updated.CurrentSha256)); Assert.Equal(2, differences.TotalChanges);
        var uses = service.FindTagReferences(updated.Id, new(updated.CurrentSha256, "Record", "WatchProgram")); Assert.Equal(2, uses.Total);
        var routinePath = service.Inspect(updated.Id, "Routine", null, 0, 100).Elements.Single(r => r.Name == "main").XPath;
        Assert.Equal("rendered", Assert.Single(service.PreviewLadder(updated.Id, new(updated.CurrentSha256, routinePath)).Rungs).Status);
    }
    [Fact]
    public async Task V38BrowserAndExtractedStructuresRetainUnsignedAndLongRealValues()
    {
        var document = XDocument.Load(source); AssertV38(document);
        XElement Scalar(string name, string type, string value) => new("Tag", new XAttribute("Name", name), new XAttribute("TagType", "Base"),
            new XAttribute("DataType", type), new XElement("Data", new XAttribute("Format", "Decorated"), new XElement("DataValue", new XAttribute("DataType", type), new XAttribute("Value", value))));
        document.Root!.Element("Controller")!.Element("Tags")!.Add(Scalar("Precise", "LREAL", "1.25"), Scalar("Unsigned", "ULINT", 18000000000000000000UL.ToString(CultureInfo.InvariantCulture)));
        document.Save(source); var project = await service.RegisterAsync(source);
        var page = service.BrowseTags(new(project.Id, project.CurrentSha256, Query: "ULINT")); Assert.Equal("Unsigned", Assert.Single(page.Nodes).TagName);
        var leaf = service.BrowseTags(new(project.Id, project.CurrentSha256, "WatchProgram", "Records", "[1].Inner.Count"));
        Assert.Equal("DINT", leaf.Selection!.DataType); Assert.True(leaf.Selection.Watchable);
        var extracted = service.ExtractStructures(new(ProjectId: project.Id)); Assert.Equal(2, extracted.Types.Length);
        Assert.Equal(18000000000000000000UL, extracted.Tags.Single(t => t.Name == "Unsigned").Value!.GetValue<ulong>());
        Assert.Equal(1.25, extracted.Tags.Single(t => t.Name == "Precise").Value!.GetValue<double>());
        AssertV38(XDocument.Load(source));
    }
    [Fact]
    public void V38ProjectProcessorAndConversionRequestsUseTheExistingSdkSurface()
    {
        static SdkCall Call(string method, object args) => new(method, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args, ProjectService.Json))!);
        SdkCatalog.ValidateCalls([Call("CreateNewProjectAsync", new { majorRevision = 38, processorTypeName = "1756-L81E", controllerName = "Generated38" }), Call("BuildAsync", new { })]);
        SdkCatalog.ValidateCalls([Call("GetProcessorTypesAsync", new { majorRev = 38 })]);
        SdkCatalog.ValidateCalls([Call("ConvertAsync", new { destinationRevision = 38 })]);
        var capabilities = JsonSerializer.SerializeToElement(service.SdkCapabilities(), ProjectService.Json);
        Assert.False(capabilities.GetProperty("controllerOperationsEnabled").GetBoolean()); Assert.Empty(capabilities.GetProperty("controllerTargets").EnumerateArray());
    }
    public void Dispose() => service.Dispose();
}
