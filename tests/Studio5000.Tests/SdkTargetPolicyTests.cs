using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class SdkTargetPolicyTests
{
    private static readonly string RoutinePath = OnlineEdits.RoutinePath("CodexTest", "main");
    private static SdkCall Call(string name, object args) => new(name, JsonSerializer.SerializeToElement(args).Deserialize<Dictionary<string, JsonElement>>()!);
    private static ControllerTarget Target(params string[] methods) => new("test", @"Broadcast\192.0.2.1", new(methods, "CodexTest", "main"));
    private static SdkCall Import() => Call("PartialImportRungsFromXmlFileAsync", new { xPath = RoutinePath, xmlFileToImport = "edit", insertPosition = 0, replaceCount = 0, onlineImportOption = "AcceptEdits" });
    private static XDocument Component() => XDocument.Parse("""
        <RSLogix5000Content><Controller Use="Context"><Tags/><Programs><Program Name="CodexTest" Use="Context">
        <Tags><Tag Name="SdkInput" TagType="Base" DataType="DINT" Usage="Local"><Data Format="Decorated"><DataValue DataType="DINT" Value="0"/></Data></Tag></Tags>
        <Routines><Routine Name="main" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text>ADD(SdkInput,1,SdkResult);</Text></Rung></RLLContent></Routine></Routines>
        </Program></Programs></Controller></RSLogix5000Content>
        """);

    [Fact]
    public void TargetCanPermitReadsAndRejectControllerWritesEvenWhenGloballyEnabled()
    {
        var target = Target("UploadToNewProjectAsync", "ReadControllerModeAsync");
        SdkTargetPolicies.ValidateConfiguration(target);
        SdkTargetPolicies.ValidateCalls(target, [Call("UploadToNewProjectAsync", new { })]);
        Assert.Equal("sdk_target_policy", Assert.Throws<OperationException>(() => SdkTargetPolicies.ValidateCalls(target, [Call("DownloadAsync", new { })])).Code);
        Assert.Throws<OperationException>(() => SdkTargetPolicies.ValidateCalls(Target("ChangeControllerModeAsync"), [Call("ChangeControllerModeAsync", new { controllerMode = "PROGRAM" })]));
        SdkTargetPolicies.ValidateCalls(new("unrestricted", "path"), [Call("DownloadAsync", new { })]);
    }

    [Theory]
    [InlineData("Controller/Tags/Tag[@Name='SdkInput']")]
    [InlineData("Controller/Programs/Program[@Name='Other']/Tags/Tag[@Name='SdkInput']")]
    [InlineData("Controller/Programs/Program[@Name='CodexTest']/Tags/Tag[@Name='SdkInput' or @Name='Other']")]
    [InlineData("Controller/Programs/Program[@Name='CodexTest']/Tags/Tag[@Name='SdkInput']/../..")]
    public void RejectsGlobalTagsForeignProgramsAndSelectorEscapes(string tagPath)
    {
        var target = Target("SetTagValueDINTAsync");
        Assert.Throws<OperationException>(() => SdkTargetPolicies.ValidateCalls(target, [Call("SetTagValueDINTAsync", new { tagPath, mode = "Online", tagValue = 1 })]));
        SdkTargetPolicies.ValidateCalls(target, [Call("SetTagValueDINTAsync", new { tagPath = "Controller/Programs/Program[@Name='CodexTest']/Tags/Tag[@Name='SdkInput']", mode = "Online", tagValue = 1 })]);
    }

    [Fact]
    public void ImportsRequireMatchingRoutineAndLocalDefinitions()
    {
        var target = Target("PartialImportRungsFromXmlFileAsync"); var call = Import();
        SdkTargetPolicies.ValidateCalls(target, [call]); SdkTargetPolicies.ValidateImport(target, call, Component());
        var contextRungs = Component(); contextRungs.Descendants("Routine").Single().Attribute("Type")!.Remove();
        SdkTargetPolicies.ValidateImport(target, call, contextRungs); // Native rung exports omit the context routine's Type.
        contextRungs.Descendants("Controller").Single().AddFirst(new XElement("DataTypes",
            new XElement("DataType", new XAttribute("Name", "BOOL"), new XAttribute("Class", "ProductDefined")),
            new XElement("DataType", new XAttribute("Name", "DINT"), new XAttribute("Class", "ProductDefined"))));
        SdkTargetPolicies.ValidateImport(target, call, contextRungs); // Native exports include built-in scalar context types.
        foreach (var mutate in new Action<XDocument>[] {
            d => d.Descendants("Controller").Single().Element("Tags")!.Add(new XElement("Tag", new XAttribute("Name", "Global"))),
            d => d.Descendants("Controller").Single().Add(new XElement("Modules", new XElement("Module"))),
            d => d.Descendants("Controller").Single().Add(new XElement("DataTypes", new XElement("DataType", new XAttribute("Name", "NewType"), new XAttribute("Class", "User")))),
            d => d.Descendants("Program").Single().SetAttributeValue("Name", "Other"),
            d => d.Descendants("Routine").Single().SetAttributeValue("Name", "Other"),
            d => d.Descendants("Routines").Single().Add(new XElement("Routine", new XAttribute("Name", "Other"))),
            d => d.Descendants("Tag").Single().SetAttributeValue("AliasFor", "Global"),
            d => d.Descendants("Tag").Single().SetAttributeValue("Usage", "Input"),
            d => d.Descendants("Controller").Single().Add(new XElement("Programs", new XElement("Program", new XAttribute("Name", "Other")))),
            d => d.Descendants("Program").Single().Add(new XElement("Tags", new XElement("Tag", new XAttribute("AliasFor", "Global")))),
            d => d.Descendants("Tag").Single().Add(new XElement("ProduceInfo")) })
        {
            var document = Component(); mutate(document);
            Assert.Equal("sdk_target_policy", Assert.Throws<OperationException>(() => SdkTargetPolicies.ValidateImport(target, call, document)).Code);
        }
        call.Arguments["xPath"] = JsonSerializer.SerializeToElement(OnlineEdits.RoutinePath("CodexTest", "other"));
        Assert.Throws<OperationException>(() => SdkTargetPolicies.ValidateCalls(target, [call]));
    }

    [Fact]
    public async Task SharedServiceRejectsDeniedMethodBeforeCreatingOrLaunchingJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "SdkTargetPolicy", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var bridge = Path.Combine(root, "bridge.exe"); File.WriteAllText(bridge, "not executable");
            using var service = new ProjectService(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root],
                NativeBridgePath = bridge, EnableControllerOperations = true, ControllerTargets = [Target("UploadToNewProjectAsync")] });
            Assert.Equal("sdk_target_policy", (await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(new([Call("DownloadAsync", new { })], ControllerTargetId: "test")))).Code);
            Assert.Empty(service.SdkJobList());
        }
        finally { Directory.Delete(root, true); }
    }
}
