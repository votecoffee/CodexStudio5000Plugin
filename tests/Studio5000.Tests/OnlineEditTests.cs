using System.Text.Json;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class OnlineEditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OnlineEditPlanTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private readonly string source;
    private readonly string import;
    private readonly string sourceHash;
    private readonly string importHash;
    public OnlineEditTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "source.acd"); import = Path.Combine(root, "edit.l5x");
        File.WriteAllText(source, "opaque fixture; planning must never try to open this ACD in the SDK");
        File.WriteAllText(import, "<RSLogix5000Content><Controller><Programs><Program Name='Main'><Routines><Routine Name='Logic' Type='RLL'/></Routines></Program></Programs></Controller></RSLogix5000Content>");
        sourceHash = ProjectService.Hash(File.ReadAllBytes(source)); importHash = ProjectService.Hash(File.ReadAllBytes(import));
        service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], EnableControllerOperations = false,
            ControllerTargets = [new("lab", @"AB_ETH-1\192.0.2.1\Backplane\0")] });
    }
    private OnlineEditPlanRequest Request() => new("lab", "Main", "Logic", new(import, importHash), sourceHash, SourcePath: source, ReplaceCount: 1);

    [Fact]
    public async Task ExplicitPathPlanningRemainsOfflineAndHonorsTheGlobalSwitch()
    {
        using var broad = new ProjectService(new() { StateDirectory = Path.Combine(root, "path-state"), AllowedRoots = [root], AllowAnyControllerPath = true });
        var plan = await broad.PrepareOnlineEditAsync(Request() with { ControllerTargetId = @"path:AB_ETH-1\192.0.2.10\Backplane\0" });
        Assert.Equal(@"AB_ETH-1\192.0.2.10\Backplane\0", plan.Target.CommunicationsPath); Assert.Null(plan.Target.Policy);
        Assert.False(plan.ControllerOperationsEnabled); Assert.Empty(broad.SdkJobList()); Assert.False(plan.FinalizationObserved);
    }

    [Fact]
    public async Task DefaultPlanAcceptsForNativeTestingWithoutConnectingOrEnablingController()
    {
        var plan = await service.PrepareOnlineEditAsync(Request());
        Assert.Equal("AcceptEdits", plan.SdkImportOption); Assert.False(plan.SdkTestEditsSupported);
        Assert.False(plan.ControllerOperationsEnabled); Assert.False(plan.NativeBridgeConfigured);
        Assert.False(plan.TestEditsObserved); Assert.False(plan.FinalizationObserved);
        Assert.Empty(service.SdkJobList());
        Assert.Equal(new[] { "GoOnlineAsync", "ReadConnectedStateAsync", "ReadControllerModeAsync", "PartialImportRungsFromXmlFileAsync", "SaveAsync" }, plan.SdkRequest.Calls.Select(c => c.Method));
        Assert.DoesNotContain(plan.SdkRequest.Calls, c => c.Method is "DownloadAsync" or "ChangeControllerModeAsync" or "GenerateSafetySignatureAsync");
        Assert.Equal("AcceptEdits", plan.SdkRequest.Calls[3].Arguments["onlineImportOption"].GetString());
        Assert.Contains(plan.StudioSteps, s => s.Contains("every edited routine", StringComparison.Ordinal));
        Assert.Contains(plan.StudioSteps, s => s.Contains("Untest", StringComparison.Ordinal));
        Assert.Equal(sourceHash, ProjectService.Hash(File.ReadAllBytes(source)));
        Assert.Equal(importHash, ProjectService.Hash(File.ReadAllBytes(import)));
        Assert.Equal(plan.PlanSha256, (await service.PrepareOnlineEditAsync(Request())).PlanSha256);
    }

    [Theory]
    [InlineData("accept_for_test", "AcceptEdits", false)]
    [InlineData("leave_pending", "LeaveEdits", false)]
    [InlineData("finalize_during_import", "FinalizeEdits", true)]
    public async Task OptionsPreserveTheDifferenceBetweenTestingAndDirectFinalization(string workflow, string option, bool direct)
    {
        var plan = await service.PrepareOnlineEditAsync(Request() with { Workflow = workflow });
        Assert.Equal(option, plan.SdkImportOption);
        Assert.Equal(direct, plan.StudioSteps.Any(s => s.Contains("no test interval", StringComparison.Ordinal)));
        Assert.False(plan.TestEditsObserved); Assert.False(plan.FinalizationObserved);
    }

    [Fact]
    public async Task NamedRoutineImportUsesExplicitTargetAndRejectsIgnoredRungRange()
    {
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { ImportKind = "routine" }));
        var plan = await service.PrepareOnlineEditAsync(Request() with { ImportKind = "routine", ReplaceCount = 0 });
        Assert.Equal("PartialImportWithTargetFromXmlFileAsync", plan.SdkRequest.Calls[3].Method);
        Assert.Equal("Logic", plan.SdkRequest.Calls[3].Arguments["targetName"].GetString());
        Assert.Equal("Controller/Programs/Program[@Name='Main']", plan.SdkRequest.Calls[3].Arguments["xPath"].GetString());
    }

    [Fact]
    public async Task ScopedRoutinePlanTargetsItsProgramAndCannotRenameOrBroadenItsPayload()
    {
        File.WriteAllText(import, "<RSLogix5000Content TargetType='Routine' TargetName='Logic'><Controller Use='Context'><Programs><Program Name='Main' Use='Context'><Routines><Routine Name='Logic' Type='RLL' Use='Target'><RLLContent/></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>");
        using var scoped = new ProjectService(new() { StateDirectory = Path.Combine(root, "scoped"), AllowedRoots = [root], ControllerTargets = [new("lab", "path",
            new(["GoOnlineAsync", "ReadConnectedStateAsync", "ReadControllerModeAsync", "PartialImportWithTargetFromXmlFileAsync", "SaveAsync"], "Main", "Logic"))] });
        var request = Request() with { ImportKind = "routine", ReplaceCount = 0, Workflow = "finalize_during_import", ImportFile = new(import, ProjectService.Hash(File.ReadAllBytes(import))) };
        var plan = await scoped.PrepareOnlineEditAsync(request);
        Assert.Equal("Controller/Programs/Program[@Name='Main']", plan.SdkRequest.Calls[3].Arguments["xPath"].GetString());
        Assert.Equal("Logic", plan.SdkRequest.Calls[3].Arguments["targetName"].GetString());
        Assert.Equal("FinalizeEdits", plan.SdkImportOption); Assert.Empty(scoped.SdkJobList());
        File.WriteAllText(import, File.ReadAllText(import).Replace("TargetType='Routine'", "TargetType='Program'"));
        Assert.Equal("sdk_target_policy", (await Assert.ThrowsAsync<OperationException>(() => scoped.PrepareOnlineEditAsync(request with { ImportFile = new(import, ProjectService.Hash(File.ReadAllBytes(import))) }))).Code);
    }

    [Fact]
    public async Task SourceTargetAndImportAreHashBoundBeforeProducingARequest()
    {
        Assert.Equal("sdk_target_unknown", (await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { ControllerTargetId = "different" }))).Code);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { ExpectedSha256 = "stale" }))).Code);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { ImportFile = new(import, "stale") }))).Code);
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { SourcePath = import, ExpectedSha256 = importHash }));
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { SourceArtifactId = Guid.NewGuid().ToString("N") }));
        Assert.Empty(service.SdkJobList());
    }

    [Theory]
    [InlineData("rungs", "[[XIC(A)XIC(B)],XIC(C)]OTE(D);")]
    [InlineData("routine", "[[XIC(A)XIC(B)],XIC(C)]OTE(D);")]
    [InlineData("rungs", "[ADD(A,1,B)]OTE(D);")]
    [InlineData("routine", "[XIC(A),]OTE(D);")]
    public async Task Import_preparation_rejects_bad_branch_grammar_before_a_native_request(string kind, string text)
    {
        File.WriteAllText(import, "<RSLogix5000Content><Controller><Programs><Program Name='Main'><Routines><Routine Name='Logic' Type='RLL'><RLLContent><Rung Number='0'><Text>NOP();</Text></Rung><Rung Number='1'><Text><![CDATA[" + text + "]]></Text></Rung></RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>");
        var before = File.ReadAllBytes(import); var request = Request() with { ImportKind = kind, ReplaceCount = kind == "routine" ? 0u : 1u, ImportFile = new(import, ProjectService.Hash(before)) };
        var error = await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(request));
        Assert.Equal("invalid_ladder", error.Code); Assert.Contains("rung 1", error.Message);
        Assert.Equal(before, File.ReadAllBytes(import)); Assert.Equal(sourceHash, ProjectService.Hash(File.ReadAllBytes(source))); Assert.Empty(service.SdkJobList());
    }

    [Theory]
    [InlineData("rungs")][InlineData("routine")]
    public async Task Valid_import_branches_ignore_array_operand_and_comment_punctuation(string kind)
    {
        File.WriteAllText(import, "<RSLogix5000Content><Controller><Programs><Program Name='Main'><Routines><Routine Name='Logic' Type='RLL'><RLLContent><Rung Number='0'><Comment><![CDATA[[not code]]]></Comment><Text><![CDATA[[ADD(Input[1,2],1,Result),XIC(C)]OTE(D);]]></Text></Rung><Rung Number='1'><Text><![CDATA[[XIC(A)XIC(B),XIC(C)]OTE(D);]]></Text></Rung></RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>");
        var before = File.ReadAllBytes(import); var plan = await service.PrepareOnlineEditAsync(Request() with { ImportKind = kind, ReplaceCount = kind == "routine" ? 0u : 1u, ImportFile = new(import, ProjectService.Hash(before)) });
        Assert.Contains("branch grammar checked", plan.CompletionEvidence); Assert.False(plan.TestEditsObserved); Assert.False(plan.FinalizationObserved);
        Assert.Equal(before, File.ReadAllBytes(import)); Assert.Empty(service.SdkJobList());
    }

    [Fact]
    public async Task WildcardScopesInvalidWorkflowsAndForeignXmlCannotBeUsedAsAnOnlinePlan()
    {
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { ProgramName = "Main' or @Name='Other" }));
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { RoutineName = "*" }));
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { Workflow = "test_by_changing_controller_mode" }));
        File.WriteAllText(import, "<gfx/>");
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(Request() with { ImportFile = new(import, ProjectService.Hash(File.ReadAllBytes(import))) }));
        File.WriteAllText(import, "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/secret'>]><RSLogix5000Content>&e;</RSLogix5000Content>");
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => service.PrepareOnlineEditAsync(Request() with { ImportFile = new(import, ProjectService.Hash(File.ReadAllBytes(import))) }));
        Assert.Empty(service.SdkJobList());
    }

    [Fact]
    public void OfflineOnlyImportIsRejectedForAnOnlineSdkSequence()
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"xPath\":\"Controller\",\"xmlFileToImport\":\"part\",\"collisionOption\":\"CancelOnColl\"}")!;
        Assert.Equal("sdk_offline_import_online", Assert.Throws<OperationException>(() => SdkCatalog.ValidateCalls([
            new("GoOnlineAsync", []), new("PartialImportFromXmlFileAsync", args)])).Code);
        SdkCatalog.ValidateCalls([new("GoOnlineAsync", []), new("GoOfflineAsync", []), new("PartialImportFromXmlFileAsync", args)]);
        var capability = JsonSerializer.SerializeToElement(OnlineEdits.Capabilities(), ProjectService.Json);
        Assert.False(capability.GetProperty("standaloneTestEdits").GetBoolean());
        Assert.False(capability.GetProperty("controllerTestModeIsTestEdits").GetBoolean());
    }

    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
