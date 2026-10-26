namespace Studio5000.Core.Resources;

// No native connection, worker or private child is owned here. The actual SDK
// process owns only a typed metadata observation and OS admission handles.
public sealed class McpFrontdoorSession : IDisposable
{
    public ResourceLedger Ledger { get; }
    public ResourceLedger.ResourceOwner Owner { get; }
    public ResourceRecord Record { get; private set; }
    private McpClientLease? lease;
    private bool closed;
    private McpFrontdoorSession(ResourceLedger ledger, ResourceLedger.ResourceOwner owner, ResourceRecord record, McpClientLease lease)
    { Ledger = ledger; Owner = owner; Record = record; this.lease = lease; }

    public static McpFrontdoorSession Open(string root, string registry, McpFrontdoorPolicy policy)
    {
        var lease = McpClientLease.AcquireFrontdoor(root, policy);
        try
        {
            var ledger = new ResourceLedger(registry, new WindowsResourceProcessDriver(), policy.MaximumMetadataClients);
            var owner = ledger.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
            foreach (var old in ledger.Records().Where(r => r.AppId == owner.AppId && !r.Terminal))
                owner.ReconcileExitedFrontdoor(old.ResourceId, old.Revision, owner.Identity.Executable);
            return new(ledger, owner, owner.AdmitFrontdoor(), lease);
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (closed) return;
        // Closing the OS lease precedes the terminal metadata attestation.
        // Failure leaves a retained claim; it never terminates this/other apps.
        Interlocked.Exchange(ref lease, null)?.Dispose();
        Record = Owner.CloseFrontdoor(Record.ResourceId, Record.Revision);
        closed = true;
    }
}
