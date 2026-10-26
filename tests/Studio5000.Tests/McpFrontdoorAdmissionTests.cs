using System.Text.Json;
using Studio5000.Core;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class McpFrontdoorAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-frontdoor-" + Guid.NewGuid().ToString("N"));
    private readonly Driver driver = new();
    private readonly ResourceLedger execution, metadata;
    private sealed class Driver : IResourceProcessDriver
    {
        internal ProcessIdentity Current = null!;
        internal readonly Dictionary<ProcessIdentity, string> States = [];
        public ProcessIdentity CurrentIdentity() => Current;
        public ProcessIdentity Capture(uint pid) => throw new InvalidOperationException("Admission must use the genuine current identity.");
        public string State(ProcessIdentity value) => value == Current ? "alive" : States.GetValueOrDefault(value, "absent");
        public IResourceProcessHandle Open(ProcessIdentity value) => throw new InvalidOperationException("Metadata cannot acquire a termination handle.");
    }
    private ProcessIdentity Identity(uint pid, string image = "frontdoor.exe") =>
        new ProcessIdentity(new('a', 64), pid, "123", Path.Combine(root, image)).Normalize();
    public McpFrontdoorAdmissionTests()
    {
        Directory.CreateDirectory(root); driver.Current = Identity(100);
        execution = new(Path.Combine(root, "registry"), driver, 16);
        metadata = new(Path.Combine(root, "registry"), driver, 8);
    }
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void Saturated_execution_claims_remain_unchanged_when_metadata_admits_a_self_observation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var owner = execution.Owner("rockwell-mcp-server");
        for (var n = 0; n < 16; n++) owner.BeginLaunch("Uncertain native fixture", "sdk-worker");
        var files = Directory.GetFiles(execution.DirectoryPath, "*.json").ToDictionary(p => p, File.ReadAllBytes);
        var frontdoor = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
        var record = frontdoor.AdmitFrontdoor();
        Assert.Equal(frontdoor.Identity, record.Owner); Assert.Null(record.Process); Assert.Equal("attached", record.Ownership); Assert.Equal("report", record.CleanupMode);
        Assert.Equal(16, execution.Records().Count(r => r.AppId == owner.AppId && !r.Terminal));
        Assert.All(files, p => Assert.Equal(p.Value, File.ReadAllBytes(p.Key)));
        Assert.Throws<InvalidOperationException>(() => owner.BeginLaunch("Still full", "sdk-worker"));
    }
    [Fact]
    public void Self_observation_is_atomic_and_detaches_only_after_its_exact_reviewed_revision()
    {
        if (!OperatingSystem.IsWindows()) return;
        var owner = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId); var record = owner.AdmitFrontdoor();
        Assert.Throws<InvalidOperationException>(() => owner.CloseFrontdoor(record.ResourceId, new('0', 64)));
        var closed = owner.CloseFrontdoor(record.ResourceId, record.Revision);
        Assert.Equal("released", closed.State); Assert.Null(closed.Receipt);
        Assert.Equal(record.Revision, closed.Reconciliation!.PreviousRevision); Assert.Equal(owner.Identity, closed.Reconciliation.Actor);
        Assert.Contains("No process termination", closed.Reconciliation.Evidence);
    }
    [Fact]
    public void Wrong_application_or_current_generation_cannot_admit_or_detach_frontdoor_state()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidOperationException>(() => execution.Owner("rockwell-mcp-server").AdmitFrontdoor());
        var owner = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId); var record = owner.AdmitFrontdoor();
        driver.Current = Identity(900);
        Assert.Throws<InvalidOperationException>(() => owner.CloseFrontdoor(record.ResourceId, record.Revision));
        Assert.Equal(record.Revision, metadata.Get(record.ResourceId).Revision);
    }
    [Theory]
    [InlineData("absent")] [InlineData("different")]
    public void Departed_metadata_generation_reconciles_without_any_process_action(string state)
    {
        if (!OperatingSystem.IsWindows()) return;
        var record = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor();
        driver.Current = Identity(200, "new.exe"); driver.States[record.Owner] = state;
        var actor = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
        var result = actor.ReconcileExitedFrontdoor(record.ResourceId, record.Revision, record.Owner.Executable);
        Assert.Equal("released", result!.State); Assert.Equal(record.Owner, result.Owner);
        Assert.Equal(record.Revision, result.Reconciliation!.PreviousRevision); Assert.Equal(actor.Identity, result.Reconciliation.Actor);
    }
    [Theory]
    [InlineData("unknown")] [InlineData("alive")] [InlineData("barrier")] [InlineData("receipt")]
    [InlineData("native")] [InlineData("private")] [InlineData("conflict")] [InlineData("image")] [InlineData("revision")]
    public void Unknown_native_changed_or_conflicting_observations_are_preserved(string condition)
    {
        if (!OperatingSystem.IsWindows()) return;
        var record = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor();
        if (condition is "alive" or "unknown") driver.States[record.Owner] = condition;
        if (condition == "barrier") record = record with { RecoveryBarrier = "Retain native uncertainty" };
        if (condition == "receipt") record = record with { Receipt = new(new('1', 32), record.Revision, "uncertain", "Retain") };
        if (condition == "native") record = record with { Kind = "sdk-session" };
        if (condition == "private") record = record with { Ownership = "private", CleanupMode = "terminate" };
        var path = Path.Combine(metadata.DirectoryPath, record.ResourceId + ".json"); var bytes = ResourceJson.Write(record); File.WriteAllBytes(path, bytes);
        if (condition == "conflict")
        {
            var other = record with { ResourceId = Guid.NewGuid().ToString("N"), AppId = "other-app", Owner = Identity(800), Process = record.Owner };
            File.WriteAllBytes(Path.Combine(metadata.DirectoryPath, other.ResourceId + ".json"), ResourceJson.Write(other));
        }
        driver.Current = Identity(200, "new.exe"); var actor = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
        Assert.Null(actor.ReconcileExitedFrontdoor(record.ResourceId, condition == "revision" ? new('0', 64) : record.Revision,
            condition == "image" ? driver.Current.Executable : record.Owner.Executable));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void Metadata_budget_is_bounded_independently_of_execution_admission()
    {
        if (!OperatingSystem.IsWindows()) return;
        for (uint i = 0; i < 8; i++) { driver.Current = Identity(100+i); metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor(); }
        driver.Current = Identity(900);
        Assert.Throws<InvalidOperationException>(() => metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor());
        Assert.DoesNotContain(execution.Records(), r => r.AppId == "rockwell-mcp-server");
    }
    [Fact]
    public void Policy_reservation_and_available_commit_floor_refuse_before_work()
    {
        var policy = new McpFrontdoorPolicy(MaximumMetadataClients: 24, MaximumStartingClients: 4, MaximumTransportCommitReservationBytes: 7516192768);
        policy.CheckReservation(23, 16UL * 1024 * 1024 * 1024);
        Assert.Throws<OperationException>(() => policy.CheckReservation(24, ulong.MaxValue));
        Assert.Throws<OperationException>(() => policy.CheckReservation(4, 1024 * 1024 * 1024));
        Assert.Throws<ArgumentException>(() => (policy with { MaximumTransportCommitReservationBytes = 1024 }).Validate());
        Assert.Throws<ArgumentException>(() => (policy with { MaximumMetadataClients = 65 }).Validate());
    }
    [Fact]
    public void Fifth_metadata_lease_coexists_with_four_legacy_clients_without_evicting_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        var legacy = Enumerable.Range(0, 4).Select(_ => McpClientLease.Acquire(root, new(4))).ToArray();
        try
        {
            using var fifth = McpClientLease.AcquireFrontdoor(root, new());
            Assert.Equal(4, fifth.Slot);
            Assert.Throws<InvalidOperationException>(() => McpClientLease.Acquire(root, new(4)));
            Assert.All(legacy, lease => Assert.InRange(lease.Slot, 0, 3));
        }
        finally { foreach (var lease in legacy) lease.Dispose(); }
    }
    [Fact]
    public void Policy_file_is_optional_for_legacy_and_duplicate_fields_are_refused()
    {
        Assert.Null(McpFrontdoorPolicy.Read(root));
        File.WriteAllText(Path.Combine(root, McpFrontdoorPolicy.FileName), JsonSerializer.Serialize(new McpFrontdoorPolicy(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.NotNull(McpFrontdoorPolicy.Read(root));
        File.WriteAllText(Path.Combine(root, McpFrontdoorPolicy.FileName), "{\"schemaVersion\":1,\"maximumMetadataClients\":8,\"maximumMetadataClients\":16}");
        Assert.Throws<InvalidOperationException>(() => McpFrontdoorPolicy.Read(root));
    }
}
