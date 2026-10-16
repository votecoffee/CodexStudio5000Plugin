using System.Text.Json;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class SdkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SdkPolicyTests", Guid.NewGuid().ToString("N"));
    private static SdkCall Call(string method, object args) => new(method, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!);
    private ProjectService Service(bool enabled = false) => new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root],
        NativeBridgePath = Path.Combine(root, "bridge.exe"), EnableControllerOperations = enabled,
        ControllerTargets = [new("lab", @"AB_ETH-1\192.0.2.1\Backplane\0")] });
    public SdkTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "bridge.exe"), "not executable; policy rejects before launch"); }

    [Fact]
    public void FrozenSurfaceHasNoUnclassifiedPublicOperations()
    {
        Assert.Equal(67, SdkCatalog.Surface.Methods.Length); Assert.Equal(59, SdkCatalog.Methods.Length);
        Assert.All(SdkCatalog.Surface.Methods, m => Assert.True(SdkCatalog.Methods.Any(c => c.Name == m.Name) || SdkCatalog.ManagedMethods.Contains(m.Name)));
        Assert.Equal(59, SdkCatalog.Describe().Length);
    }

    [Theory]
    [InlineData("Online")]
    [InlineData("Offline")]
    public void AcceptsExactOperationMode(string mode) => SdkCatalog.ValidateCalls([Call("GetTagValueDINTAsync", new { tagPath = "Controller/Tags/Tag[@Name='Count']", mode })]);

    [Theory]
    [InlineData("online")]
    [InlineData("1")]
    [InlineData("SystemCommand")]
    public void RejectsUnknownEnumAndMissingArguments(string mode)
    {
        var ex = Assert.Throws<OperationException>(() => SdkCatalog.ValidateCalls([Call("SetTagValueDINTAsync", new { tagPath = "x", mode, tagValue = 1 })]));
        Assert.Equal("sdk_argument_invalid", ex.Code);
        Assert.Equal("sdk_argument_missing", Assert.Throws<OperationException>(() => SdkCatalog.ValidateCalls([Call("SetTagValueDINTAsync", new { mode = "Offline" })])).Code);
    }

    [Fact]
    public void IntegerBoundariesRawBytesAndUnknownMethodsAreValidated()
    {
        SdkCatalog.ValidateCalls([Call("SetTagValueULINTAsync", new { tagPath = "x", mode = "Offline", tagValue = ulong.MaxValue })]);
        Assert.Throws<OperationException>(() => SdkCatalog.ValidateCalls([Call("SetTagValueSINTAsync", new { tagPath = "x", mode = "Offline", tagValue = 128 })]));
        SdkCatalog.ValidateCalls([Call("SetTagValueAsync", new { tagPath = "x", mode = "Offline", type = "BYTE_ARRAY", tagValue = "AAECAw==" })]);
        Assert.Throws<OperationException>(() => SdkCatalog.ValidateCalls([Call("SetTagValueAsync", new { tagPath = "x", mode = "Offline", type = "BYTE_ARRAY", tagValue = "bad base64!" })]));
        Assert.Throws<OperationException>(() => SdkCatalog.ValidateCalls([Call("InvokeAnyMethod", new { })]));
        Assert.Throws<OperationException>(() => SdkCatalog.ValidateCalls([Call("BuildAsync", new { executable = "cmd" })]));
        Assert.Throws<OperationException>(() => SdkCatalog.ValidateCalls([Call("SaveAsAsync", new { force = true })]));
    }

    [Fact]
    public async Task AllControllerEntryPointsAreDisabledByDefaultBeforeNativeLaunch()
    {
        using var service = Service();
        foreach (var call in new[] { Call("DownloadAsync", new { }), Call("GoOnlineAsync", new { }), Call("LoadImageFromSDCardAsync", new { }),
            Call("GenerateSafetySignatureAsync", new { }), Call("GetTagValueBOOLAsync", new { tagPath = "x", mode = "Online" }),
            Call("SetTagValueBOOLAsync", new { tagPath = "x", mode = "Online", tagValue = true }) })
        {
            var ex = await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(new([call])));
            Assert.Equal("sdk_controller_disabled", ex.Code);
        }
        Assert.Empty(service.SdkJobList());
    }

    [Fact]
    public async Task EnabledControllerCallsStillRequireAnExactConfiguredTarget()
    {
        using var service = Service(true);
        var ex = await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(new([Call("DownloadAsync", new { })], ControllerTargetId: "unknown")));
        Assert.Equal("sdk_target_unknown", ex.Code);
        ex = await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(new([Call("DownloadAsync", new { })])));
        Assert.Equal("sdk_controller_disabled", ex.Code);
    }

    [Fact]
    public async Task RejectsStaleInputOutputTraversalAndXmlEntitiesBeforeDispatch()
    {
        using var service = Service(); var source = Path.Combine(root, "source.acd"); File.WriteAllText(source, "fixture");
        var hash = ProjectService.Hash(File.ReadAllBytes(source));
        var ex = await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(new([Call("BuildAsync", new { })], SourcePath: source, ExpectedSha256: "wrong")));
        Assert.Equal("stale_revision", ex.Code);
        foreach (var output in new[] { "../outside.acd", "C:\\outside.acd", "final.acd", "input.acd", "created.acd", "CON.acd", "bad.exe" })
            await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(new([Call("SaveAsAsync", new { savePath = output })], SourcePath: source, ExpectedSha256: hash)));
        var xml = Path.Combine(root, "evil.xml"); File.WriteAllText(xml, "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/secret'>]><x>&e;</x>");
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => service.StartSdkJobAsync(new([Call("PartialImportFromXmlFileAsync", new { xPath = "Controller", xmlFileToImport = "part", collisionOption = "CancelOnColl" })],
            SourcePath: source, ExpectedSha256: hash, InputFiles: new() { ["part"] = new(xml, ProjectService.Hash(File.ReadAllBytes(xml))) })));
        Assert.Empty(service.SdkJobList()); Assert.Equal(hash, ProjectService.Hash(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RestartMarksIncompleteControllerJobIndeterminate()
    {
        var id = Guid.NewGuid().ToString("N"); var dir = Path.Combine(root, "state", "sdk-jobs", id); Directory.CreateDirectory(dir);
        var job = new SdkJob(id, "running", DateTimeOffset.UtcNow, ["DownloadAsync"], "hash", "lab", true, [], [], []);
        File.WriteAllText(Path.Combine(dir, "job.json"), JsonSerializer.Serialize(job, ProjectService.Json));
        using var service = Service(); Assert.Equal("indeterminate", service.SdkJob(id).Status);
        Assert.Contains("restarted", service.SdkJob(id).Error!);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
