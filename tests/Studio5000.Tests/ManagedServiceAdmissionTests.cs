using System.Text.Json;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class ManagedServiceAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-native-admission-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact]
    public void Native_admission_requires_exact_surviving_parent_claim_and_preinitialized_barrier()
    {
        var driver = new ResourceLedgerTests.Driver(); var ledger = new ResourceLedger(root, driver);
        var parent = ledger.Owner("rockwell-mcp-server"); driver.Live[100] = ResourceLedgerTests.Driver.Identity(100, "200");
        var record = parent.BindProcess(parent.BeginLaunch("Service", cleanupMode: "terminate").ResourceId, 100, "windows_job");
        byte[] Metadata(string ownerId, string profile) => JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, resourceId = record.ResourceId, ownerId, pid = parent.Identity.Pid, resourceProfile = profile });
        driver.Actor = driver.Live[100];
        Assert.False(ManagedServiceAdmission.ValidateReceipt(Metadata(parent.OwnerId, ManagedServiceAdmission.Profile), ledger));
        driver.Actor = parent.Identity; parent.SetRecoveryBarrier(record.ResourceId, ManagedServiceAdmission.ShutdownBarrier); driver.Actor = driver.Live[100];
        Assert.True(ManagedServiceAdmission.ValidateReceipt(Metadata(parent.OwnerId, ManagedServiceAdmission.Profile), ledger));
        Assert.False(ManagedServiceAdmission.ValidateReceipt(Metadata(new('b', 32), ManagedServiceAdmission.Profile), ledger));
        Assert.False(ManagedServiceAdmission.ValidateReceipt(Metadata(parent.OwnerId, "offline-contained-v1"), ledger));
        driver.Live.Remove(parent.Identity.Pid);
        Assert.False(ManagedServiceAdmission.ValidateReceipt(Metadata(parent.OwnerId, ManagedServiceAdmission.Profile), ledger));
    }
}
