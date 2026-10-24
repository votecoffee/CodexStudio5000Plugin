using System.Collections.Concurrent;
using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class OfflineServiceBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-offline-bootstrap-" + Guid.NewGuid().ToString("N"));
    private string Backend => Path.Combine(root, "backend");
    private readonly Driver driver = new();
    private ResourceLedger Ledger => new(Path.Combine(root, "registry"), driver, 16);
    private static ProcessIdentity Identity(uint pid, string image, string created = "123") => new ProcessIdentity(new('a', 64), pid, created, image).Normalize();
    public OfflineServiceBootstrapTests()
    {
        Directory.CreateDirectory(root); Directory.CreateDirectory(Backend); Directory.CreateDirectory(Path.Combine(root, ".local"));
        driver.Actor = Identity(42, Path.Combine(root, "Studio5000.Mcp.exe"));
        var paths = new[] { OfflineServicePin.Service, "artifacts/app/Service/Studio5000.Service.dll", "artifacts/app/Service/Studio5000.Core.dll",
            OfflineServicePin.Worker, "artifacts/app/ExtensionWorker/Studio5000.ExtensionWorker.dll", OfflineServicePin.Script };
        foreach (var relative in paths) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "Harmless pin fixture " + relative); }
        var pin = new OfflineServicePin(1, "26.1022.174219", paths.Select(p => new OfflinePinnedFile(p, new FileInfo(Path.Combine(root, p)).Length, OfflineServicePin.Hash(Path.Combine(root, p)))).ToArray(), []);
        File.WriteAllText(Path.Combine(Backend, OfflineServicePin.FileName), JsonSerializer.Serialize(pin, OfflineServicePin.Json));
        WriteSettings();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private void WriteSettings(string property = "", object? value = null)
    {
        var options = new Dictionary<string, object?> { ["EnableControllerOperations"] = false, ["EnableSdkServiceRecovery"] = false,
            ["AutoStartSdkService"] = false, ["NativeBridgePath"] = null, ["CipBridgePath"] = null, ["ExtensionWorkerPath"] = OfflineServicePin.Worker };
        if (property != "") options[property] = value;
        File.WriteAllText(Path.Combine(root, "service.local.json"), JsonSerializer.Serialize(new { ServiceUrl = "http://127.0.0.1:5487", Studio5000 = options }));
    }
    private sealed class Driver : IResourceProcessDriver
    {
        public ProcessIdentity Actor = null!;
        public readonly ConcurrentDictionary<ProcessIdentity, string> States = new();
        public readonly Dictionary<uint, ProcessIdentity> Captures = new();
        public ProcessIdentity CurrentIdentity() => Actor;
        public ProcessIdentity Capture(uint pid) => Captures[pid];
        public string State(ProcessIdentity identity) => identity == Actor ? "alive" : States.GetValueOrDefault(identity, "absent");
        public IResourceProcessHandle Open(ProcessIdentity identity) => throw new InvalidOperationException("No termination handles in bootstrap");
    }
    private sealed class Starter(OfflineServiceBootstrapTests fixture) : IOfflineServiceStarter
    {
        public int Starts; public int PolicyChecks; public bool UnsafeEffectivePolicy; public bool Healthy; public bool Unregistered; public bool Fail; public bool FailAfterOwner;
        public ProcessIdentity? ServiceIdentity;
        public Task<OfflineServiceHealth?> ProbeAsync(Uri url, CancellationToken ct) => Task.FromResult(Healthy ? new OfflineServiceHealth("ok", "26.1022.174219") : null);
        public bool HasUnregisteredServiceProcess(string expectedExecutable) => Unregistered;
        public Task VerifyEffectivePolicyAsync(string root, OfflineBootstrapSettings settings, CancellationToken ct)
        { PolicyChecks++; if (UnsafeEffectivePolicy) throw new InvalidOperationException("Effective policy is not offline"); return Task.CompletedTask; }
        public async Task<int> StartAsync(string root, string output, Action<ProcessIdentity> started, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Starts); if (Fail) throw new IOException("Synthetic starter uncertainty");
            started(Identity(500, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", n.ToString()));
            await Task.Delay(20, ct); Publish(n);
            if (FailAfterOwner) throw new IOException("Starter observation lost after service registered");
            return 0;
        }
        public void Publish(int generation = 1, string? image = null)
        {
            ServiceIdentity = Identity(100, image ?? Path.Combine(fixture.root, OfflineServicePin.Service), generation.ToString()); fixture.driver.States[ServiceIdentity] = "alive";
            File.WriteAllText(Path.Combine(fixture.root, ".local/service-owner.json"), JsonSerializer.Serialize(new OfflineServiceOwner(1, new('c', 32), ServiceIdentity, "fixture-registry"), OfflineServicePin.Json)); Healthy = true;
        }
    }
    private Task<OfflineServiceReady> Ensure(Starter starter, TimeSpan? timeout = null) => OfflineServiceBootstrap.EnsureAsync(root, Backend, Ledger.Owner("rockwell-mcp-server"), starter, timeout ?? TimeSpan.FromSeconds(3), default);
    [Fact]
    public async Task Absent_service_starts_once_and_real_self_published_owner_is_reused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this); var first = await Ensure(starter); var second = await Ensure(starter);
        Assert.True(first.Started); Assert.False(second.Started); Assert.Equal(1, starter.Starts); Assert.Equal(starter.ServiceIdentity, first.Owner);
        using var journal = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ".local/offline-service-start.json")));
        Assert.Equal("observed", journal.RootElement.GetProperty("phase").GetString());
        Assert.Equal(driver.Actor.Pid, journal.RootElement.GetProperty("observer").GetProperty("pid").GetUInt32());
        Assert.NotEqual(driver.Actor.Pid, journal.RootElement.GetProperty("service").GetProperty("owner").GetProperty("pid").GetUInt32());
    }
    [Fact]
    public async Task Competing_frontends_serialize_to_one_service_start()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this); var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Ensure(starter)));
        Assert.Equal(1, starter.Starts); Assert.Single(results, r => r.Started); Assert.All(results, r => Assert.Equal(starter.ServiceIdentity, r.Owner));
    }
    [Fact]
    public async Task Previous_service_diagnostics_are_preserved_before_the_pinned_script_can_replace_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(root, ".local/service.stderr.log"), "Retain prior failure evidence");
        await Ensure(new Starter(this));
        var copy = Assert.Single(Directory.GetFiles(Path.Combine(root, ".local/offline-service-start-history"), "*-previous-service.stderr.log"));
        Assert.Equal("Retain prior failure evidence", File.ReadAllText(copy));
    }
    [Fact]
    public async Task Healthy_owner_with_unsafe_loaded_policy_is_preserved_without_starting_or_serving_requests()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this) { UnsafeEffectivePolicy = true }; starter.Publish();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(starter));
        Assert.Equal(0, starter.Starts); Assert.Equal(1, starter.PolicyChecks); Assert.Equal("alive", driver.State(starter.ServiceIdentity!));
        Assert.False(File.Exists(Path.Combine(root, ".local/offline-service-start.json")));
    }
    [Fact]
    public async Task Verified_owner_loss_after_observed_start_allows_a_new_service_generation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this); var before = await Ensure(starter); driver.States[before.Owner] = "absent"; starter.Healthy = false;
        var next = await Ensure(starter); Assert.True(next.Started); Assert.Equal(2, starter.Starts); Assert.NotEqual(before.Owner, next.Owner);
        Assert.True(Directory.GetFiles(Path.Combine(root, ".local/offline-service-start-history"), "*.json").Length >= 6);
    }
    [Fact]
    public async Task Request_gate_ensures_a_new_generation_on_demand_after_owner_loss()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this); var gate = new OfflineServiceGate(() => Ensure(starter), driver);
        var first = await gate.Ready(); driver.States[first.Owner] = "absent"; starter.Healthy = false;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => gate.Ready()));
        Assert.Equal(2, starter.Starts); Assert.All(results, r => Assert.NotEqual(first.Owner, r.Owner));
    }
    [Fact]
    public async Task Request_gate_can_inspect_failed_readiness_without_replaying_uncertain_start()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this) { Fail = true }; var gate = new OfflineServiceGate(() => Ensure(starter), driver);
        await Assert.ThrowsAsync<IOException>(() => gate.Ready());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.Ready());
        Assert.Equal(1, starter.Starts);
    }
    [Fact]
    public async Task Uncertain_dispatch_is_retained_and_not_automatically_replayed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this) { Fail = true }; await Assert.ThrowsAsync<IOException>(() => Ensure(starter));
        var bytes = File.ReadAllBytes(Path.Combine(root, ".local/offline-service-start.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(starter)); Assert.Equal(1, starter.Starts);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, ".local/offline-service-start.json")));
    }
    [Fact]
    public async Task Actual_healthy_owner_resolves_lost_starter_observation_without_replay()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this) { FailAfterOwner = true }; await Assert.ThrowsAsync<IOException>(() => Ensure(starter));
        driver.Actor = driver.Actor with { Created = "999" }; var result = await Ensure(starter);
        Assert.False(result.Started); Assert.Equal(1, starter.Starts);
        using var journal = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ".local/offline-service-start.json")));
        Assert.Equal("999", journal.RootElement.GetProperty("observer").GetProperty("created").GetString());
        Assert.Equal("123", journal.RootElement.GetProperty("actor").GetProperty("created").GetString());
    }
    [Theory]
    [InlineData("EnableControllerOperations", true)]
    [InlineData("EnableSdkServiceRecovery", true)]
    [InlineData("AutoStartSdkService", true)]
    [InlineData("NativeBridgePath", "sdk.exe")]
    [InlineData("CipBridgePath", "cip.exe")]
    [InlineData("ExtensionWorkerPath", "other.exe")]
    public async Task Native_or_unpinned_configuration_never_dispatches_startup(string property, object value)
    {
        if (!OperatingSystem.IsWindows()) return;
        WriteSettings(property, value); var starter = new Starter(this);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(starter)); Assert.Equal(0, starter.Starts);
    }
    [Theory]
    [InlineData("runtime")]
    [InlineData("extra")]
    [InlineData("hold")]
    [InlineData("management")]
    [InlineData("duplicate")]
    public async Task Drift_hold_or_ambiguous_metadata_refuses_before_start(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (failure == "runtime") File.AppendAllText(Path.Combine(root, OfflineServicePin.Service), "drift");
        if (failure == "extra") File.WriteAllText(Path.Combine(root, "artifacts/app/Service/extra.dll"), "unknown");
        if (failure == "hold") File.WriteAllText(Path.Combine(root, ".local/maintenance-hold.json"), "{}");
        if (failure == "management") File.WriteAllText(Path.Combine(root, ".local/management-runtime.json"), "{}");
        if (failure == "duplicate") File.WriteAllText(Path.Combine(root, "service.local.json"), "{\"ServiceUrl\":\"http://127.0.0.1:5487\",\"serviceurl\":\"http://127.0.0.1:5487\"}");
        var starter = new Starter(this); await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(starter)); Assert.Equal(0, starter.Starts);
    }
    [Theory]
    [InlineData("foreign")]
    [InlineData("unknown")]
    [InlineData("listener")]
    public async Task Existing_unmatched_owner_or_listener_is_not_adopted_or_restarted(string state)
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this);
        if (state != "listener") starter.Publish(image: state == "foreign" ? Path.Combine(root, "foreign.exe") : null);
        else starter.Healthy = true;
        if (state == "unknown") driver.States[starter.ServiceIdentity!] = "unknown";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(starter)); Assert.Equal(0, starter.Starts);
    }
    [Fact]
    public async Task Unregistered_existing_service_causes_bounded_wait_without_duplicate_start()
    {
        if (!OperatingSystem.IsWindows()) return;
        var starter = new Starter(this) { Unregistered = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ensure(starter, TimeSpan.FromMilliseconds(250))); Assert.Equal(0, starter.Starts);
        Assert.False(File.Exists(Path.Combine(root, ".local/offline-service-start.json")));
    }
    private (ResourceLedger Ledger, ResourceRecord Worker, ResourceLedger.ResourceOwner Actor) ClosedWorker()
    {
        var ledger = Ledger; driver.Actor = Identity(60, Path.Combine(root, OfflineServicePin.Service));
        var old = ledger.Owner("rockwell-mcp-server"); var intent = old.BeginLaunch("Old extension fixture", "extension-worker", cleanupMode: "terminate");
        driver.Captures[200] = Identity(200, Path.Combine(root, OfflineServicePin.Worker));
        var worker = old.BindProcess(intent.ResourceId, 200, "windows_job");
        driver.Actor = Identity(42, Path.Combine(root, "Studio5000.Mcp.exe"));
        return (ledger, worker, ledger.Owner("rockwell-mcp-server"));
    }
    [Fact]
    public void Dead_pinned_workers_recover_with_real_actor_and_eight_old_unbound_records_untouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, worker, actor) = ClosedWorker(); var retained = new Dictionary<string, byte[]>();
        for (var n = 0; n < 8; n++)
        {
            var record = actor.BeginLaunch("Retained unexplained launch", "mcp-adapter", cleanupMode: "terminate");
            var file = Path.Combine(ledger.DirectoryPath, record.ResourceId + ".json"); retained[file] = File.ReadAllBytes(file);
        }
        OfflineServiceBootstrap.PrepareAdmission(root, Backend, actor);
        var recovered = ledger.Get(worker.ResourceId); Assert.Equal("released", recovered.State);
        Assert.Equal(worker.Revision, recovered.Reconciliation!.PreviousRevision); Assert.Equal(actor.Identity, recovered.Reconciliation.Actor);
        Assert.Equal(worker.Owner, recovered.Owner); Assert.Equal(worker.Process, recovered.Process);
        Assert.All(retained, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }
    [Theory]
    [InlineData("live-owner")]
    [InlineData("unknown-child")]
    [InlineData("native")]
    [InlineData("barrier")]
    [InlineData("foreign-member")]
    [InlineData("conflict")]
    public void Worker_recovery_preserves_live_uncertain_native_or_shared_claims(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, worker, actor) = ClosedWorker();
        if (failure == "live-owner") driver.States[worker.Owner] = "alive";
        if (failure == "unknown-child") driver.States[worker.Process!] = "unknown";
        if (failure == "native") worker = worker with { Kind = "sdk-worker" };
        if (failure == "barrier") worker = worker with { RecoveryBarrier = "Uncertain operation; preserve" };
        if (failure == "foreign-member") worker = worker with { Members = [Identity(300, Path.Combine(root, "user-owned.exe"))] };
        var path = Path.Combine(ledger.DirectoryPath, worker.ResourceId + ".json"); File.WriteAllBytes(path, ResourceJson.Write(worker));
        if (failure == "conflict")
        {
            var attached = worker with { ResourceId = Guid.NewGuid().ToString("N"), AppId = "another-app", Ownership = "shared", CleanupMode = "report" };
            File.WriteAllBytes(Path.Combine(ledger.DirectoryPath, attached.ResourceId + ".json"), ResourceJson.Write(attached));
        }
        var bytes = File.ReadAllBytes(path); OfflineServiceBootstrap.PrepareAdmission(root, Backend, actor);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void Reviewed_predecessor_images_allow_only_exact_exited_frontend_generation_recovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = Path.Combine(root, "previous"); Directory.CreateDirectory(previous);
        var names = new[] { "Studio5000.StdioHost.exe", "Studio5000.Mcp.exe", "Studio5000.Core.dll" };
        foreach (var name in names) File.WriteAllText(Path.Combine(previous, name), "Harmless predecessor " + name);
        var file = Path.Combine(Backend, OfflineServicePin.FileName);
        var pin = JsonSerializer.Deserialize<OfflineServicePin>(File.ReadAllBytes(file), OfflineServicePin.Json)!;
        pin = pin with { PreviousFrontends = [new(previous, names.Select(name => new OfflinePinnedFile(name, new FileInfo(Path.Combine(previous, name)).Length, OfflineServicePin.Hash(Path.Combine(previous, name)))).ToArray())] };
        File.WriteAllText(file, JsonSerializer.Serialize(pin, OfflineServicePin.Json));
        var ledger = Ledger; driver.Actor = Identity(60, Path.Combine(previous, names[0])); driver.Captures[200] = Identity(200, Path.Combine(previous, names[1]));
        var old = ledger.Owner("rockwell-mcp-server"); var intent = old.BeginLaunch("Old pinned frontend", "mcp-adapter", cleanupMode: "terminate"); var before = old.BindProcess(intent.ResourceId, 200, "windows_job");
        driver.Actor = Identity(42, Path.Combine(root, "Studio5000.Mcp.exe")); var actor = ledger.Owner("rockwell-mcp-server");
        OfflineServiceBootstrap.PrepareAdmission(root, Backend, actor);
        var retired = ledger.Get(before.ResourceId); Assert.Equal("released", retired.State); Assert.Equal(actor.Identity, retired.Reconciliation!.Actor);
        Assert.Equal(before.Revision, retired.Reconciliation.PreviousRevision); Assert.Equal(before.Owner, retired.Owner);
    }
    [Theory]
    [InlineData("get")]
    [InlineData("post")]
    [InlineData("download")]
    public async Task Service_requests_wait_for_readiness_before_connection_or_any_dispatch(string method)
    {
        var calls = 0; using var client = new ServiceClient(root, ensureReady: _ => { calls++; throw new InvalidOperationException("Expected bootstrap fence"); });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (method == "get") await client.GetAsync<object>("/not-dispatched");
            else if (method == "post") await client.PostAsync<object>("/not-dispatched", new { });
            else await client.DownloadAsync("not-dispatched");
        });
        Assert.Equal("Expected bootstrap fence", error.Message); Assert.Equal(1, calls);
    }
}
