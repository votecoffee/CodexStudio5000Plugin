using Studio5000.Core;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class NativeGuiHandoffTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-shell-policy-" + Guid.NewGuid().ToString("N"));
    private readonly ResourceLedgerTests.Driver driver = new();
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Shell(ProcessIdentity identity) : IUserShellHandoff
    {
        public ProcessIdentity ProviderIdentity => identity;
        public int Calls; public bool Closed; public bool Fail;
        public void Launch(string executable) { Calls++; if (Fail) throw new IOException("Synthetic lost shell result"); }
        public void Dispose() => Closed = true;
    }
    private (ResourceLedger ledger, NativeGuiHandoff handoff, Shell shell, string executable) Fixture()
    {
        Directory.CreateDirectory(root); var executable = Path.Combine(root, "generated-never-executed.exe"); File.WriteAllText(executable, "Fixture bytes only");
        var ledger = new ResourceLedger(Path.Combine(root, "registry"), driver);
        var shellIdentity = ResourceLedgerTests.Driver.Identity(1, "50"); driver.Live[1] = shellIdentity;
        var shell = new Shell(shellIdentity);
        var sessions = new NativeSessionRegistry(ledger.Owner("rockwell-fixture"), Path.Combine(root, "sessions"));
        return (ledger, new(sessions, Path.Combine(root, "handoffs"), () => shell), shell, executable);
    }
    [Fact]
    public void Handoff_detaches_only_shell_connection_and_leaves_user_software_alive()
    {
        var (ledger, handoff, shell, executable) = Fixture();
        var result = handoff.Launch(executable);
        Assert.Equal("handed_off", result.Status); Assert.Equal("user", result.ApplicationOwnership); Assert.Equal("not_returned_by_shell", result.TargetInstanceIdentity);
        Assert.Equal(1, shell.Calls); Assert.True(shell.Closed); Assert.Equal(0, driver.Effects); Assert.Equal("alive", driver.State(shell.ProviderIdentity));
        var claim = ledger.Get(result.ResourceId); Assert.Equal("attached", claim.Ownership); Assert.Equal("released", claim.State); Assert.NotNull(claim.BarrierResolution);
    }
    [Fact]
    public void Lost_handoff_is_retained_and_cannot_be_replayed_as_another_launch()
    {
        var (ledger, handoff, shell, executable) = Fixture(); shell.Fail = true;
        Assert.Throws<IOException>(() => handoff.Launch(executable));
        Assert.NotNull(ledger.Records().Single().RecoveryBarrier); Assert.True(shell.Closed);
        var error = Assert.Throws<OperationException>(() => handoff.Launch(executable)); Assert.Equal("native_handoff_uncertain", error.Code);
        Assert.Equal(1, shell.Calls); Assert.Equal(0, driver.Effects);
    }
}
