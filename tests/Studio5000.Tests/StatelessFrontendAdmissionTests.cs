using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class StatelessFrontendAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-bound-admission-" + Guid.NewGuid().ToString("N"));
    private readonly Driver driver = new();
    private static ProcessIdentity Identity(uint pid, string image) => new ProcessIdentity(new('a', 64), pid, "123", image).Normalize();
    private sealed class Driver : IResourceProcessDriver
    {
        public ProcessIdentity Actor = Identity(42, @"C:\fixture\Studio5000.StdioHost.exe");
        public bool Denied;
        public ProcessIdentity CurrentIdentity() => Actor;
        public ProcessIdentity Capture(uint pid) => Denied ? throw new InvalidOperationException("Unknown process") : Identity(pid, @"C:\fixture\Studio5000.Mcp.exe");
        public string State(ProcessIdentity identity) => "alive";
        public IResourceProcessHandle Open(ProcessIdentity identity) => throw new InvalidOperationException("Admission must not open a termination handle.");
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static string Id() => Guid.NewGuid().ToString("N");

    [Fact]
    public void Publishes_one_active_full_identity_without_an_unbound_phase()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = new ResourceLedger(root, driver, 1); var owner = ledger.Owner("rockwell-mcp-server"); var process = driver.Capture(100);
        var result = owner.AdmitSuspendedFrontend(Id(), "Suspended fixture", process);
        Assert.Equal("active", result.State); Assert.Equal(process, result.Process); Assert.Equal(owner.Identity, result.Owner);
        Assert.Equal("windows_job", result.Containment); Assert.Equal("mcp-adapter", result.Kind); Assert.Single(ledger.Records());
    }

    [Fact]
    public void Repeated_capacity_refusal_creates_no_new_claim_or_changes_old_uncertainty()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = new ResourceLedger(root, driver, 1); var owner = ledger.Owner("rockwell-mcp-server");
        var old = owner.BeginLaunch("Retain unexplained old launch", "mcp-adapter", cleanupMode: "terminate");
        var path = Path.Combine(ledger.DirectoryPath, old.ResourceId + ".json"); var bytes = File.ReadAllBytes(path);
        for (var n = 0; n < 32; n++) Assert.Throws<InvalidOperationException>(() => owner.AdmitSuspendedFrontend(Id(), "Refused fixture", driver.Capture(100)));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Single(ledger.Records());
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("identity")]
    [InlineData("owner")]
    [InlineData("foreign")]
    [InlineData("closed")]
    public void Unknown_or_ineligible_admission_leaves_no_reservation(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = new ResourceLedger(root, driver); var owner = ledger.Owner(failure == "foreign" ? "another-app" : "rockwell-mcp-server");
        var identity = driver.Capture(100);
        if (failure == "capture") driver.Denied = true;
        if (failure == "identity") identity = identity with { Created = "999" };
        if (failure == "owner") driver.Actor = driver.Actor with { Created = "999" };
        if (failure == "closed") owner.FenceAndInspectClose();
        Assert.Throws<InvalidOperationException>(() => owner.AdmitSuspendedFrontend(Id(), "Ineligible fixture", identity)); Assert.Empty(ledger.Records());
    }

    [Fact]
    public void Competing_admissions_cannot_oversubscribe_or_leave_failed_reservations()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = new ResourceLedger(root, driver, 1); var owner = ledger.Owner("rockwell-mcp-server"); var admitted = 0;
        Parallel.For(0, 24, n =>
        {
            try { owner.AdmitSuspendedFrontend(Id(), "Competing fixture", driver.Capture((uint)(100 + n))); Interlocked.Increment(ref admitted); }
            catch (InvalidOperationException) { }
        });
        Assert.Equal(1, admitted); var record = Assert.Single(ledger.Records()); Assert.NotNull(record.Process); Assert.Equal("active", record.State);
    }
}
