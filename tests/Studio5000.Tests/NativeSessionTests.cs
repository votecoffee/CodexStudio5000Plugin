using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class NativeSessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-native-policy-" + Guid.NewGuid().ToString("N"));
    private readonly ResourceLedgerTests.Driver driver = new();
    private (ResourceLedger, NativeSessionRegistry) Registry()
    {
        var ledger = new ResourceLedger(Path.Combine(root, "registry"), driver);
        return (ledger, new(ledger.Owner("rockwell-fixture"), Path.Combine(root, "native")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact]
    public void Shared_connection_only_closes_on_last_local_lease_and_never_terminates_software()
    {
        var (ledger, registry) = Registry(); var identity = new NativeSoftwareIdentity("test-sdk", "explicit-session-a", "sdk-session");
        var one = registry.Attach(identity); var two = registry.Attach(identity); Assert.Same(one, two);
        var calls = 0;
        NativeCloseResult Close() { calls++; return NativeCloseResult.Closed("Synthetic save/disconnect/dispose adapter verified"); }
        Assert.Equal("retained_shared", one.Close(Close).Outcome); Assert.Equal(0, calls); Assert.Equal(1, two.State.References);
        Assert.Equal("closed", two.Close(Close).Outcome); Assert.Equal(1, calls); Assert.Equal(0, driver.Effects);
        var record = ledger.Get(one.ResourceId); Assert.Equal("released", record.State); Assert.NotNull(record.BarrierResolution);
        Assert.Throws<InvalidOperationException>(() => one.Close(Close)); Assert.Equal(1, calls);
    }
    [Fact]
    public void Dirty_refusal_keeps_the_connection_and_requires_explicit_review()
    {
        var (ledger, registry) = Registry(); var lease = registry.Attach(new("test-com", "explicit-instance", "com-instance"), "attached");
        Assert.Equal("refused", lease.Close(() => NativeCloseResult.Refused("Unsaved document; no native close dispatched")).Outcome);
        Assert.NotNull(ledger.Get(lease.ResourceId).RecoveryBarrier); Assert.Equal("active", ledger.Get(lease.ResourceId).State);
        Assert.Throws<InvalidOperationException>(() => lease.Close(() => throw new Exception("Must not be dispatched")));
        lease.ReviewRefusal("User completed synthetic save; adapter will read back before detach");
        Assert.Equal("closed", lease.Close(() => NativeCloseResult.Closed("Synthetic exact document readback and detach")).Outcome);
        Assert.Equal(0, driver.Effects);
    }
    [Fact]
    public void Lost_native_close_and_incomplete_save_attestation_remain_uncertain()
    {
        var (ledger, registry) = Registry(); var lease = registry.Attach(new("test-wmi", "explicit-provider-instance", "wmi-instance"));
        Assert.Equal("uncertain", lease.Close(() => throw new IOException("Lost native result")).Outcome);
        Assert.Throws<InvalidOperationException>(() => lease.Close(() => throw new Exception("Replayed")));
        Assert.Throws<InvalidOperationException>(() => lease.ReviewRefusal("Cannot override unknown native effects"));
        Assert.NotNull(ledger.Get(lease.ResourceId).RecoveryBarrier);
        var other = registry.Attach(new("test-sdk", "another-session", "sdk-session"));
        Assert.Equal("uncertain", other.Close(() => new("closed", false, true, "Missing saved document evidence")).Outcome);
        Assert.Equal(0, driver.Effects);
    }
    [Fact]
    public void Explicit_user_software_attachment_does_not_adopt_a_reused_pid()
    {
        var (_, registry) = Registry(); var actual = ResourceLedgerTests.Driver.Identity(100, "200"); driver.Live[100] = actual;
        Assert.Throws<InvalidOperationException>(() => registry.Attach(new("test-native", "explicit", "native-process", actual with { Created = "199" })));
        var lease = registry.Attach(new("test-native", "explicit", "native-process", actual), "attached");
        Assert.Equal("closed", lease.Close(() => NativeCloseResult.Closed("Detached app observer; user software left open")).Outcome);
        Assert.Equal("alive", driver.State(actual)); Assert.Equal(0, driver.Effects);
    }
    [Fact]
    public void Worker_checkpoints_preserve_pending_effects_and_exact_close_identity()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "worker.json"); var session = new string('b', 32);
        using var journal = new NativeLifecycleJournal(path, session, driver.Actor, "rockwell-logix-sdk");
        journal.BeforeStep(0, "SetTagValueAsync");
        Assert.Equal("uncertain", NativeLifecycleJournal.VerifyClose(path, session, driver.Actor, true).Outcome);
        journal.AfterStep(); Assert.True(File.Exists(path + ".000.completed.json"));
        journal.Complete(true, true, true);
        Assert.Equal("closed", NativeLifecycleJournal.VerifyClose(path, session, driver.Actor, true).Outcome);
        Assert.Equal("uncertain", NativeLifecycleJournal.VerifyClose(path, session, driver.Actor with { Created = "101" }, true).Outcome);
        Assert.Equal("uncertain", NativeLifecycleJournal.VerifyClose(path, session, driver.Actor, false).Outcome);
        Assert.Equal("uncertain", NativeLifecycleJournal.VerifyClose(path, new('c', 32), driver.Actor, true).Outcome);
    }
}
