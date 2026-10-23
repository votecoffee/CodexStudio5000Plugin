using System.Text.Json;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class NativeExecutionPolicyTests
{
    private static SdkCall Call(string method, object? arguments = null) => new(method,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments ?? new { }))!);
    private static ServiceOptions Options() => NativeExecutionPolicy.CreateLocalOptions(Path.Combine(Path.GetTempPath(), "rockwell-local-policy-fixture"));

    [Fact]
    public void Generated_local_configuration_is_restricted_without_any_native_or_scm_start()
    {
        var options = Options();
        Assert.False(options.EnableControllerOperations || options.AllowAnyControllerPath || options.AllowAnySourcePath || options.AllowAnyOutputPath);
        Assert.False(options.EnableSdkServiceRecovery || options.AutoStartSdkService || options.EnableNativeGuiLaunch);
        Assert.Empty(options.ControllerTargets); Assert.Null(options.CipBridgePath);
        Assert.Equal(NativeExecutionPolicy.LocalProject, options.NativeExecutionProfile);
        Assert.Equal(8, options.MaximumOwnedProcesses); Assert.Equal(16, options.MaximumOwnedResources);
        Assert.Throws<ArgumentException>(() => NativeExecutionPolicy.CreateLocalOptions("relative"));
    }
    [Theory]
    [InlineData("EnableControllerOperations")] [InlineData("AllowAnyControllerPath")]
    [InlineData("AllowAnySourcePath")] [InlineData("AllowAnyOutputPath")]
    [InlineData("EnableSdkServiceRecovery")] [InlineData("AutoStartSdkService")] [InlineData("EnableNativeGuiLaunch")]
    public void A_local_profile_cannot_silently_widen_its_boolean_permissions(string property)
    {
        var options = Options(); typeof(ServiceOptions).GetProperty(property)!.SetValue(options, true);
        Assert.Throws<ArgumentException>(() => NativeExecutionPolicy.ValidateOptions(options));
    }
    [Fact]
    public void Controller_targets_and_cip_are_rejected_even_when_controller_flag_is_false()
    {
        var options = Options(); options.ControllerTargets = [new("synthetic", "synthetic")];
        Assert.Throws<ArgumentException>(() => NativeExecutionPolicy.ValidateOptions(options));
        options.ControllerTargets = []; options.CipBridgePath = "synthetic.exe";
        Assert.Throws<ArgumentException>(() => NativeExecutionPolicy.ValidateOptions(options));
    }
    [Theory]
    [InlineData("GoOnlineAsync")] [InlineData("UploadAsync")] [InlineData("DownloadAsync")]
    [InlineData("SetCommunicationsPathAsync")] [InlineData("ChangeControllerModeAsync")]
    [InlineData("SetTagValueDINTAsync")] [InlineData("GenerateSafetySignatureAsync")]
    [InlineData("PartialImportRungsFromXmlFileAsync")]
    public void Nonlocal_methods_are_refused_before_worker_or_native_access(string method)
    {
        Assert.Equal("native_local_operation_denied", Assert.Throws<OperationException>(() =>
            NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, [Call(method)])).Code);
    }
    [Fact]
    public void Discrete_read_export_import_build_methods_are_declared_and_explicitly_offline()
    {
        foreach (var method in NativeExecutionPolicy.LocalMethods) Assert.NotNull(SdkCatalog.Find(method));
        var calls = new[] { Call("OpenLogixProjectAsync"), Call("PartialExportToXmlFileAsync"),
            Call("PartialImportFromXmlFileAsync"), Call("BuildAsync"), Call("GetTagValueDINTAsync", new { mode = "Offline" }) };
        NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, calls);
        foreach (var mode in new[] { "Online", "", "offline" }) Assert.Throws<OperationException>(() =>
            NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, [Call("GetTagValueDINTAsync", new { mode })]));
        Assert.Throws<OperationException>(() => NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, [Call("GetTagValueDINTAsync")]));
        Assert.Throws<OperationException>(() => NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, calls, "synthetic"));
        Assert.Throws<OperationException>(() => NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, calls, controllerTargetId: "synthetic"));
    }
    [Theory]
    [InlineData("Online")] [InlineData("Connected")] [InlineData("Unknown")]
    public void An_unexpected_connection_is_not_forcibly_changed_by_the_local_profile(string state)
    {
        Assert.Equal("native_local_connected", Assert.Throws<OperationException>(() => NativeExecutionPolicy.ValidateProjectConnection(NativeExecutionPolicy.LocalProject, state)).Code);
        NativeExecutionPolicy.ValidateProjectConnection(NativeExecutionPolicy.Standard, state);
    }
    [Fact]
    public void Worker_profile_must_match_its_exact_owner_and_gui_is_rejected_before_shell_admission()
    {
        NativeExecutionPolicy.ValidateWorkerProfile(NativeExecutionPolicy.LocalProject, NativeExecutionPolicy.LocalProject);
        Assert.Throws<OperationException>(() => NativeExecutionPolicy.ValidateWorkerProfile(NativeExecutionPolicy.Standard, NativeExecutionPolicy.LocalProject));
        Assert.Throws<ArgumentException>(() => NativeExecutionPolicy.ValidateName("unknown"));
        var options = Options(); options.NativeExecutionProfile = NativeExecutionPolicy.Standard;
        Assert.Throws<OperationException>(() => NativeExecutionPolicy.RequireGui(options));
        options.NativeExecutionProfile = NativeExecutionPolicy.LocalProject; options.EnableNativeGuiLaunch = true;
        Assert.Throws<OperationException>(() => NativeExecutionPolicy.RequireGui(options));
    }
    [Fact]
    public async Task Shared_service_refuses_controller_and_gui_requests_before_any_worker_is_available()
    {
        var root = Path.Combine(Path.GetTempPath(), "rockwell-local-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = NativeExecutionPolicy.CreateLocalOptions(root);
            using var service = new ProjectService(options);
            Assert.Equal("native_local_operation_denied", (await Assert.ThrowsAsync<OperationException>(() =>
                service.StartSdkJobAsync(new([Call("GoOnlineAsync")])))).Code);
            Assert.Equal("native_local_operation_denied", (await Assert.ThrowsAsync<OperationException>(() =>
                service.StartSdkJobAsync(new([Call("GetProcessorTypesAsync", new { majorRev = 35 })], ControllerTargetId: "synthetic")))).Code);
            Assert.Equal("native_gui_disabled", Assert.Throws<OperationException>(() => service.LaunchFactoryTalkStudio()).Code);
            Assert.Equal("native_unavailable", (await Assert.ThrowsAsync<OperationException>(() =>
                service.StartSdkJobAsync(new([Call("GetProcessorTypesAsync", new { majorRev = 35 })])))).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
