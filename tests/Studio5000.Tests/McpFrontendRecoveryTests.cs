using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class McpFrontendRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-frontend-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly Driver driver = new();
    private static string Host => Path.GetFullPath(@"C:\fixture\Studio5000.StdioHost.exe");
    private static string Adapter => Path.GetFullPath(@"C:\fixture\Studio5000.Mcp.exe");
    private static ProcessIdentity Identity(uint pid, string image, string created = "200") => new ProcessIdentity(new('a', 64), pid, created, image).Normalize();
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Driver : IResourceProcessDriver
    {
        internal ProcessIdentity Actor = Identity(42, Host, "100");
        internal readonly Dictionary<ProcessIdentity, string> States = new();
        public ProcessIdentity CurrentIdentity() => Actor;
        public ProcessIdentity Capture(uint pid) => Identity(pid, Adapter);
        public string State(ProcessIdentity identity) => identity == Actor ? "alive" : States.GetValueOrDefault(identity, "absent");
        public IResourceProcessHandle Open(ProcessIdentity identity) => throw new InvalidOperationException("Frontend recovery must never open a process handle.");
    }
    private (ResourceLedger Ledger, ResourceLedger.ResourceOwner Actor, ResourceRecord Prior) Closed(int maximum = 16)
    {
        var ledger = new ResourceLedger(root, driver, maximum);
        var old = ledger.Owner("rockwell-mcp-server");
        var intent = old.BeginLaunch("MCP fixture", "mcp-adapter", cleanupMode: "terminate");
        var prior = old.BindProcess(intent.ResourceId, 100, "windows_job");
        prior = old.PublishMembers(prior.ResourceId, [Identity(101, Adapter)]);
        driver.Actor = Identity(43, Host, "300");
        return (ledger, ledger.Owner("rockwell-mcp-server"), prior);
    }
    private void Replace(ResourceLedger ledger, ResourceRecord record) => File.WriteAllBytes(Path.Combine(ledger.DirectoryPath, record.ResourceId + ".json"), ResourceJson.Write(record));

    [Fact]
    public void Exact_absent_transport_releases_capacity_with_revision_and_real_actor_attestation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, actor, prior) = Closed(1);
        var retired = Assert.Single(McpFrontendRecovery.ReconcileClosed(actor, Adapter));
        Assert.Equal("released", retired.State); Assert.Equal(prior.Process, retired.Process); Assert.Equal(prior.Owner, retired.Owner);
        Assert.Equal(prior.Revision, retired.Reconciliation!.PreviousRevision);
        Assert.Equal(actor.OwnerId, retired.Reconciliation.ActorOwnerId); Assert.Equal(driver.Actor, retired.Reconciliation.Actor);
        Assert.Empty(McpFrontendRecovery.ReconcileClosed(actor, Adapter)); Assert.Equal(retired.Revision, ledger.Get(prior.ResourceId).Revision);
        Assert.NotNull(actor.BeginLaunch("Next transport", "mcp-adapter", cleanupMode: "terminate"));
    }

    [Fact]
    public void Reused_pid_only_retires_the_old_full_creation_identity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (_, actor, prior) = Closed();
        driver.States[prior.Owner] = "different"; driver.States[prior.Process!] = "different";
        Assert.Single(McpFrontendRecovery.ReconcileClosed(actor, Adapter));
    }

    [Theory]
    [InlineData("owner-alive")][InlineData("owner-unknown")][InlineData("child-alive")][InlineData("child-unknown")]
    [InlineData("member-alive")][InlineData("member-unknown")][InlineData("foreign-member")][InlineData("foreign-host")]
    [InlineData("foreign-adapter")][InlineData("unbound")][InlineData("shared")][InlineData("attached")]
    [InlineData("barrier")][InlineData("receipt")][InlineData("native-kind")][InlineData("foreign-app")]
    [InlineData("uncertain")][InlineData("report-only")][InlineData("not-contained")][InlineData("resolved-native-barrier")]
    public void Live_unknown_unbound_native_and_foreign_claims_remain_byte_identical(string refusal)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, actor, prior) = Closed();
        switch (refusal)
        {
            case "owner-alive": driver.States[prior.Owner] = "alive"; break;
            case "owner-unknown": driver.States[prior.Owner] = "unknown"; break;
            case "child-alive": driver.States[prior.Process!] = "alive"; break;
            case "child-unknown": driver.States[prior.Process!] = "unknown"; break;
            case "member-alive": driver.States[prior.Members[0]] = "alive"; break;
            case "member-unknown": driver.States[prior.Members[0]] = "unknown"; break;
            case "foreign-member": prior = prior with { Members = [Identity(101, @"C:\fixture\NativeEditor.exe")] }; break;
            case "foreign-host": prior = prior with { Owner = prior.Owner with { Executable = @"C:\other\Studio5000.StdioHost.exe" } }; break;
            case "foreign-adapter": prior = prior with { Process = prior.Process! with { Executable = @"C:\other\Studio5000.Mcp.exe" } }; break;
            case "unbound": prior = prior with { State = "launching", Process = null, Members = [] }; break;
            case "shared": prior = prior with { Ownership = "shared", CleanupMode = "report" }; break;
            case "attached": prior = prior with { Ownership = "attached", CleanupMode = "report" }; break;
            case "barrier": prior = prior with { RecoveryBarrier = "Unresolved native work" }; break;
            case "receipt": prior = prior with { Receipt = new(new('b', 32), prior.Revision, "uncertain", "No replay") }; break;
            case "native-kind": prior = prior with { Kind = "sdk-worker" }; break;
            case "foreign-app": prior = prior with { AppId = "another-app" }; break;
            case "uncertain": prior = prior with { State = "uncertain" }; break;
            case "report-only": prior = prior with { CleanupMode = "report" }; break;
            case "not-contained": prior = prior with { Containment = "app_owned" }; break;
            case "resolved-native-barrier": prior = prior with { BarrierResolution = new(new('b', 32), driver.Actor, prior.Revision, "Prior native evidence", 1) }; break;
        }
        Replace(ledger, prior); var before = File.ReadAllBytes(Path.Combine(ledger.DirectoryPath, prior.ResourceId + ".json"));
        Assert.Empty(McpFrontendRecovery.ReconcileClosed(actor, Adapter));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(ledger.DirectoryPath, prior.ResourceId + ".json")));
    }

    [Fact]
    public void Another_retained_claim_blocks_recovery_even_if_both_processes_are_gone()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, actor, prior) = Closed();
        var shared = actor.BeginLaunch("Retained native attachment", "native-session", "shared");
        Replace(ledger, shared with { Process = prior.Process, State = "active", RecoveryBarrier = "Preserve uncertainty" });
        Assert.Empty(McpFrontendRecovery.ReconcileClosed(actor, Adapter));
        Assert.Equal(prior.Revision, ledger.Get(prior.ResourceId).Revision);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Fenced_or_impersonated_actor_cannot_reconcile(bool impersonated)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (ledger, actor, prior) = Closed();
        if (impersonated) driver.Actor = Identity(44, Host, "400"); else actor.FenceAndInspectClose();
        Assert.Throws<InvalidOperationException>(() => McpFrontendRecovery.ReconcileClosed(actor, Adapter));
        Assert.Equal(prior.Revision, ledger.Get(prior.ResourceId).Revision);
    }
}
