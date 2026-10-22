using System.Text;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class ResourceLedgerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-resource-model-" + Guid.NewGuid().ToString("N"));
    private readonly Driver driver = new();
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private ResourceLedger Ledger(int maximum = 32) => new(root, driver, maximum);
    internal sealed class Driver : IResourceProcessDriver
    {
        internal ProcessIdentity Actor = Identity(42, "100");
        internal readonly Dictionary<uint, ProcessIdentity> Live = new();
        internal int Effects;
        internal bool FailAfterEffect;
        internal Driver() => Live.Add(42, Actor);
        internal static ProcessIdentity Identity(uint pid, string created = "200") => new ProcessIdentity(new string('a', 64), pid, created, @"C:\fixture\worker.exe").Normalize();
        public ProcessIdentity CurrentIdentity() => Actor;
        public ProcessIdentity Capture(uint pid) => Live[pid];
        public string State(ProcessIdentity identity) => Live.TryGetValue(identity.Pid, out var observed) ? observed == identity ? "alive" : "different" : "absent";
        public IResourceProcessHandle Open(ProcessIdentity identity)
        {
            Assert.Equal("alive", State(identity)); return new Handle(this, identity);
        }
        private sealed class Handle(Driver driver, ProcessIdentity identity) : IResourceProcessHandle
        {
            public ProcessIdentity Identity => identity;
            public bool Alive => driver.State(identity) == "alive";
            public void Terminate() { driver.Effects++; driver.Live.Remove(identity.Pid); if (driver.FailAfterEffect) throw new IOException("Synthetic lost result"); }
            public bool Wait(int milliseconds) => !Alive;
            public void Dispose() { }
        }
    }
    private (ResourceLedger Ledger, ResourceLedger.ResourceOwner Owner, ResourceRecord Record) Bound(string ownership = "private", string cleanup = "terminate")
    {
        var ledger = Ledger(); var owner = ledger.Owner("rockwell-fixture");
        driver.Live.Add(100, Driver.Identity(100));
        var intent = owner.BeginLaunch("Generated worker 🔧", ownership: ownership, cleanupMode: cleanup);
        return (ledger, owner, owner.BindProcess(intent.ResourceId, 100));
    }
    [Fact]
    public void Model_normalizes_identity_defaults_unicode_and_python_float_tokens()
    {
        var record = new ResourceRecord { ResourceId = new('b', 32), AppId = "rockwell-fixture", OwnerId = new('c', 32),
            Owner = Driver.Identity(42, "0100"), Label = "München 🔧\n<&>", CreatedAt = 1, UpdatedAt = -0.0 };
        var bytes = ResourceJson.Write(record); var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\\u00fc", text); Assert.Contains("\\ud83d\\udd27", text); Assert.Contains("\"created_at\":1.0", text); Assert.Contains("\"updated_at\":-0.0", text);
        Assert.Equal("100", ResourceJson.Read(bytes).Owner.Created);
        Assert.Equal(record.Revision, ResourceJson.Read(bytes).Revision);
        foreach (var version in new[] { "true", "1.0", "\"1\"", "2" })
            Assert.ThrowsAny<Exception>(() => ResourceJson.Read(Encoding.UTF8.GetBytes(text.Replace("\"schema_version\":1", "\"schema_version\":" + version, StringComparison.Ordinal))));
        Assert.ThrowsAny<Exception>(() => ResourceJson.Read(Encoding.UTF8.GetBytes(text.Replace("\"app_id\":", "\"extra\":1,\"app_id\":", StringComparison.Ordinal))));
    }
    [Fact]
    public void Service_can_retire_an_inner_job_but_attachments_and_siblings_still_block_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = new ResourceLedger(root, driver, nestedServiceOwner: true);
        var outerOwner = ledger.Owner("rockwell-fixture");
        driver.Live[100] = Driver.Identity(100, "200"); driver.Live[101] = Driver.Identity(101, "300");
        var outer = outerOwner.BindProcess(outerOwner.BeginLaunch("Outer service job", cleanupMode: "terminate").ResourceId, 100, "windows_job");
        driver.Actor = driver.Live[100]; var serviceOwner = ledger.Owner("rockwell-fixture");
        var child = serviceOwner.BindProcess(serviceOwner.BeginLaunch("Inner worker", cleanupMode: "terminate").ResourceId, 101, "windows_job");
        driver.Actor = outerOwner.Identity;
        outerOwner.PublishMembers(outer.ResourceId, [driver.Live[100], driver.Live[101]]);
        Assert.Throws<InvalidOperationException>(() => outerOwner.Cleanup(outer.ResourceId, ledger.Get(outer.ResourceId).Revision, new('d', 32)));
        driver.Actor = serviceOwner.Identity;
        var attachment = serviceOwner.BindProcess(serviceOwner.BeginLaunch("User attachment", ownership: "attached").ResourceId, 101);
        Assert.Throws<InvalidOperationException>(() => serviceOwner.Cleanup(child.ResourceId, child.Revision, new('d', 32)));
        serviceOwner.ReleaseAttachment(attachment.ResourceId);
        Assert.Equal("terminated", serviceOwner.Cleanup(child.ResourceId, child.Revision, new('d', 32)).Outcome);
        Assert.Equal(1, driver.Effects); Assert.Equal("active", ledger.Get(outer.ResourceId).State);
    }
    [Fact]
    public void Records_survive_reopen_and_cleanup_has_one_durable_effect()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, owner, record) = Bound();
        var request = new string('d', 32); var receipt = owner.Cleanup(record.ResourceId, record.Revision, request);
        Assert.Equal("terminated", receipt.Outcome); Assert.Equal(1, driver.Effects);
        Assert.Equal(receipt, Ledger().Get(record.ResourceId).Receipt);
        Assert.Equal(receipt, owner.Cleanup(record.ResourceId, record.Revision, request));
        Assert.Throws<InvalidOperationException>(() => owner.Cleanup(record.ResourceId, ledger.Get(record.ResourceId).Revision, new('e', 32)));
        Assert.Equal(1, driver.Effects);
    }
    [Theory]
    [InlineData("attached", "report")] [InlineData("shared", "report")] [InlineData("private", "report")]
    public void Nonterminable_claims_never_reach_process_control(string ownership, string cleanup)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (_, owner, record) = Bound(ownership, cleanup);
        Assert.Throws<InvalidOperationException>(() => owner.Cleanup(record.ResourceId, record.Revision, new('d', 32)));
        Assert.Equal(0, driver.Effects);
        if (ownership != "private") Assert.Equal("released", owner.ReleaseAttachment(record.ResourceId).State);
        else Assert.Throws<InvalidOperationException>(() => owner.ReleaseAttachment(record.ResourceId));
    }
    [Fact]
    public void Unknown_result_is_retained_and_reconciliation_keeps_receipt_and_barrier()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, owner, record) = Bound(); driver.FailAfterEffect = true;
        var receipt = owner.Cleanup(record.ResourceId, record.Revision, new('d', 32));
        Assert.Equal("uncertain", receipt.Outcome);
        record = owner.SetRecoveryBarrier(record.ResourceId, "Inspect native outcome");
        Assert.Throws<InvalidOperationException>(() => owner.ResolveRecoveryBarrier(record.ResourceId, record.Revision, "Cannot clear a cleanup receipt"));
        Assert.Throws<InvalidOperationException>(() => owner.Cleanup(record.ResourceId, record.Revision, new('e', 32)));
        var retired = owner.Reconcile(record.ResourceId, record.Revision, "Synthetic driver independently confirms the resource is gone");
        Assert.Equal("released", retired.State); Assert.Equal(receipt, retired.Receipt); Assert.NotNull(retired.RecoveryBarrier); Assert.NotNull(retired.Reconciliation);
        Assert.Equal(1, driver.Effects); Assert.Equal(retired.Revision, ledger.Get(record.ResourceId).Revision);
    }
    [Fact]
    public void Shared_opaque_sessions_can_be_detached_without_inventing_a_pid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = Ledger(); var owner = ledger.Owner("rockwell-fixture");
        var intent = owner.BeginLaunch("Native session connection", "sdk-session", "shared");
        var active = owner.ActivateOpaqueAttachment(intent.ResourceId); Assert.Null(active.Process); Assert.Equal("active", active.State);
        Assert.Equal("released", owner.ReleaseAttachment(intent.ResourceId).State); Assert.Equal(0, driver.Effects);
    }
    [Fact]
    public void Admission_generation_and_foreign_claims_are_not_adopted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = Ledger(1); var owner = ledger.Owner("rockwell-fixture"); var intent = owner.BeginLaunch("Pending launch");
        Assert.Throws<InvalidOperationException>(() => ledger.Owner("rockwell-fixture").BeginLaunch("Duplicate generation"));
        Assert.Throws<InvalidOperationException>(() => ledger.Owner("rockwell-fixture").SetRecoveryBarrier(intent.ResourceId, "Cannot impersonate"));
        owner.FenceAndInspectClose(); Assert.Throws<InvalidOperationException>(() => owner.BeginLaunch("Closed owner"));
        Assert.Equal(0, driver.Effects);
    }
    [Fact]
    public void Native_driver_foreign_host_is_unknown_before_any_local_pid_control()
    {
        var native = new WindowsResourceProcessDriver(); var foreign = Driver.Identity(42) with { Pid = 0, HostId = new('f', 64) };
        Assert.Equal("unknown", native.State(foreign));
        Assert.Throws<InvalidOperationException>(() => native.Open(foreign));
    }
    [Fact]
    public void A_second_attachment_blocks_private_cleanup_until_that_claim_is_released()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, owner, record) = Bound(); var peer = ledger.Owner("rockwell-fixture");
        var claim = peer.BeginLaunch("Protected attachment", ownership: "attached"); peer.BindProcess(claim.ResourceId, 100);
        Assert.Throws<InvalidOperationException>(() => owner.Cleanup(record.ResourceId, record.Revision, new('d', 32)));
        Assert.Equal(0, driver.Effects);
        peer.ReleaseAttachment(claim.ResourceId);
        Assert.Equal("terminated", owner.Cleanup(record.ResourceId, record.Revision, new('d', 32)).Outcome);
        Assert.Equal(1, driver.Effects);
    }
    [Fact]
    public void Reused_pid_retires_the_old_claim_without_touching_the_replacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (_, owner, record) = Bound(); var replacement = Driver.Identity(100, "300"); driver.Live[100] = replacement;
        Assert.Equal("already_exited", owner.Cleanup(record.ResourceId, record.Revision, new('d', 32)).Outcome);
        Assert.Equal(replacement, driver.Live[100]); Assert.Equal(0, driver.Effects);
    }
    [Fact]
    public void Missing_recovery_timestamp_is_not_defaulted_to_zero()
    {
        var record = new ResourceRecord { ResourceId = new('b', 32), AppId = "rockwell-fixture", OwnerId = new('c', 32), Owner = Driver.Identity(42, "100"), Label = "Generated",
            Reconciliation = new(new('d', 32), Driver.Identity(42, "100"), new('e', 64), "Synthetic", 0) };
        var node = System.Text.Json.Nodes.JsonNode.Parse(ResourceJson.Write(record))!;
        Assert.True(node["reconciliation"]!.AsObject().Remove("recorded_at"));
        Assert.ThrowsAny<Exception>(() => ResourceJson.Read(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }
}
