using System.Runtime.InteropServices;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

// Synthetic SCM only. These tests never query, start or stop a Windows service.
public sealed class SdkServiceControlTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-sdk-service-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceOptions options = new();
    private readonly Driver driver = new();
    private SdkServiceControl Control(bool held = false) => new(options, root, driver, () => held);
    private static SdkServiceEnsureRequest Request() => new(Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Driver : ISdkServiceDriver
    {
        public string State = "Stopped", Name = SdkServiceControl.ServiceName;
        public bool Installed = true; public int Starts, Reads, PendingReads;
        public Action? OnStart; public uint ExitCode;
        public SdkServiceSnapshot Read()
        {
            Reads++;
            if (State == "StartPending" && PendingReads > 0 && --PendingReads == 0) State = "Running";
            return new(Name, SdkServiceControl.DisplayName, State, new('a', 64), Win32ExitCode: ExitCode, Installed: Installed);
        }
        public void Start() { Starts++; State = "Running"; OnStart?.Invoke(); }
    }
    [Fact]
    public async Task Running_is_a_retained_noop_even_if_same_request_is_repeated_after_service_stops()
    {
        driver.State = "Running"; var control = Control(); var request = Request();
        var receipt = await control.EnsureRunningAsync(request); Assert.Equal("already_running", receipt.Outcome); Assert.False(receipt.StartDispatched);
        driver.State = "Stopped"; Assert.Equal(receipt, await Control().EnsureRunningAsync(request));
        Assert.Equal(0, driver.Starts); Assert.Equal(1, driver.Reads);
    }
    [Fact]
    public async Task Stopped_service_is_started_once_and_read_back_without_controller_access()
    {
        var control = Control(); var request = Request();
        var receipt = await control.EnsureRunningAsync(request);
        Assert.Equal("started", receipt.Outcome); Assert.Equal("Stopped", receipt.Before!.State); Assert.Equal("Running", receipt.After!.State);
        Assert.Equal(SdkServiceControl.ServiceName, receipt.ServiceName); Assert.Equal(new string('a', 64), receipt.HostId);
        Assert.Equal(receipt, await control.EnsureRunningAsync(request)); Assert.Equal(1, driver.Starts); Assert.Equal(0, control.ActiveRequests);
    }
    [Fact]
    public async Task Concurrent_requests_share_one_start_and_then_observe_running()
    {
        driver.OnStart = () => { driver.State = "StartPending"; driver.PendingReads = 3; };
        var control = Control();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => control.EnsureRunningAsync(Request())));
        Assert.Equal(1, driver.Starts); Assert.Single(receipts, r => r.Outcome == "started");
        Assert.Equal(7, receipts.Count(r => r.Outcome == "already_running")); Assert.Equal(0, control.ActiveRequests);
    }
    [Theory]
    [InlineData("Paused")] [InlineData("StopPending")] [InlineData("ContinuePending")] [InlineData("Unknown")]
    public async Task Non_stopped_states_never_receive_a_start_or_stop(string state)
    {
        driver.State = state;
        Assert.Equal("refused", (await Control().EnsureRunningAsync(Request())).Outcome); Assert.Equal(0, driver.Starts);
    }
    [Fact]
    public async Task Maintenance_and_settings_are_checked_before_a_start()
    {
        var error = await Assert.ThrowsAsync<OperationException>(() => Control(held: true).EnsureRunningAsync(Request()));
        Assert.Equal("maintenance_hold", error.Code); Assert.Equal(0, driver.Reads);
        options.EnableSdkServiceRecovery = false;
        Assert.Equal("sdk_service_recovery_disabled", (await Control().EnsureRunningAsync(Request())).Code); Assert.Equal(0, driver.Starts);
        options.AutoStartSdkService = false; var reads = driver.Reads;
        await Control().EnsureForSdkAsync(default); Assert.Equal(reads, driver.Reads);
    }
    [Theory]
    [InlineData(5, "sdk_service_access_denied")] [InlineData(1058, "sdk_service_disabled")]
    public async Task Known_windows_rejections_have_actionable_retained_receipts(int code, string expected)
    {
        driver.OnStart = () => throw new SdkServiceApiException(code, true);
        var control = Control(); var request = Request(); var result = await control.EnsureRunningAsync(request);
        Assert.Equal("failed", result.Outcome); Assert.Equal(expected, result.Code);
        Assert.Equal(result, await control.EnsureRunningAsync(request)); Assert.Equal(1, driver.Starts);
    }
    [Fact]
    public async Task Lost_start_result_survives_recreation_and_blocks_replacement_start()
    {
        driver.OnStart = () => { driver.State = "StartPending"; throw new IOException("Synthetic lost Windows response"); };
        var request = Request(); var receipt = await Control().EnsureRunningAsync(request);
        Assert.Equal("uncertain", receipt.Outcome); Assert.Equal(receipt, await Control().EnsureRunningAsync(request));
        driver.State = "Stopped";
        var error = await Assert.ThrowsAsync<OperationException>(() => Control().EnsureRunningAsync(Request()));
        Assert.Equal("sdk_service_recovery_uncertain", error.Code); Assert.Equal(1, driver.Starts);
        Assert.Contains(request.RequestId, Control().Status().UncertainRequestIds);
    }
    [Fact]
    public async Task Cancelled_start_is_not_replayed_and_native_preflight_will_not_replay_a_job()
    {
        using var cancel = new CancellationTokenSource(); driver.OnStart = cancel.Cancel;
        var receipt = await Control().EnsureRunningAsync(Request(), ct: cancel.Token);
        Assert.Equal("uncertain", receipt.Outcome); Assert.Equal(1, driver.Starts);
        driver.State = "Stopped";
        await Assert.ThrowsAsync<OperationException>(() => Control().EnsureForSdkAsync(default)); Assert.Equal(1, driver.Starts);
    }
    [Fact]
    public async Task Pending_start_can_be_observed_without_duplicate_control()
    {
        driver.State = "StartPending"; driver.PendingReads = 3;
        var receipt = await Control().EnsureRunningAsync(Request());
        Assert.Equal("already_running", receipt.Outcome); Assert.False(receipt.StartDispatched); Assert.Equal(0, driver.Starts);
    }
    [Fact]
    public async Task Timeout_retains_unknown_start_and_never_retries()
    {
        options.SdkServiceStartTimeoutSeconds = 1; driver.OnStart = () => driver.State = "StartPending";
        var result = await Control().EnsureRunningAsync(Request());
        Assert.Equal("uncertain", result.Outcome); Assert.Equal("sdk_service_start_timeout", result.Code); Assert.Equal(1, driver.Starts);
    }
    [Fact]
    public async Task Startup_failure_keeps_windows_exit_evidence_and_is_not_retried()
    {
        driver.OnStart = () => { driver.State = "Stopped"; driver.ExitCode = 1067; };
        var result = await Control().EnsureRunningAsync(Request());
        Assert.Equal("failed", result.Outcome); Assert.Equal("sdk_service_start_failed", result.Code); Assert.Equal(1067u, result.After!.Win32ExitCode);
        Assert.Equal(1, driver.Starts);
    }
    [Fact]
    public async Task Native_preflight_is_enabled_by_default_and_can_be_disabled_independently()
    {
        await Control().EnsureForSdkAsync(default); Assert.Equal(1, driver.Starts);
        options.AutoStartSdkService = false; driver.State = "Stopped";
        await Control().EnsureForSdkAsync(default); Assert.Equal(1, driver.Starts);
    }
    [Fact]
    public async Task Missing_or_changed_service_identity_never_starts_another_target()
    {
        driver.Installed = false; driver.State = "NotInstalled";
        Assert.Equal("sdk_service_missing", (await Control().EnsureRunningAsync(Request())).Code);
        driver.Installed = true; driver.Name = "AnotherService";
        Assert.Equal("sdk_service_identity_mismatch", (await Assert.ThrowsAsync<OperationException>(() => Control().EnsureRunningAsync(Request()))).Code);
        Assert.Equal(0, driver.Starts);
    }
    [Fact]
    public async Task Invalid_request_ids_cannot_escape_the_receipt_directory()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Control().EnsureRunningAsync(new("../other"))); Assert.Equal(0, driver.Reads);
    }
    [Fact]
    public async Task Shared_project_service_exposes_the_same_core_implementation_and_shutdown_fence()
    {
        var workspace = Path.Combine(root, "workspace"); options.StateDirectory = workspace; options.AllowedRoots = [root];
        using var service = new ProjectService(options, sdkServiceDriver: driver, sdkServiceMaintenanceHeld: () => false);
        Assert.Equal("Stopped", service.SdkServiceStatus().Service.State);
        Assert.Equal("started", (await service.EnsureSdkServiceRunningAsync(Request())).Outcome);
        Assert.True(service.TryFenceShutdown());
        Assert.Equal("service_stopping", (await Assert.ThrowsAsync<OperationException>(() => service.EnsureSdkServiceRunningAsync(Request()))).Code);
    }
    [Fact]
    public async Task Recovery_remains_available_during_a_project_job_and_blocks_shutdown_until_finished()
    {
        options.StateDirectory = Path.Combine(root, "workspace"); options.AllowedRoots = [root];
        using var service = new ProjectService(options, sdkServiceDriver: driver, sdkServiceMaintenanceHeld: () => false);
        var workspaceGate = (WorkspaceCoordinator)typeof(ProjectService).GetField("gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
        await workspaceGate.WaitAsync(default);
        using var cancel = new CancellationTokenSource();
        driver.OnStart = () => driver.State = "StartPending";
        try
        {
            var recovery = service.EnsureSdkServiceRunningAsync(Request(), cancel.Token);
            Assert.Equal(1, driver.Starts); Assert.False(service.TryFenceShutdown());
            Assert.True(service.ManagementActivity().NativeJobs > 0);
            cancel.Cancel(); Assert.Equal("uncertain", (await recovery).Outcome);
        }
        finally { workspaceGate.Release(); }
        Assert.True(service.TryFenceShutdown());
    }
    [Fact]
    public void Native_status_structure_matches_the_documented_windows_abi()
    {
        var status = typeof(WindowsSdkServiceDriver).GetNestedType("ServiceStatus", System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(36, Marshal.SizeOf(status));
    }
}
