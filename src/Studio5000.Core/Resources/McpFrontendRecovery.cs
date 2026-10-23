namespace Studio5000.Core.Resources;

/// <summary>Preserve and reconcile exited stateless transports before reserving another slot.</summary>
public static class McpFrontendRecovery
{
    public static ResourceRecord[] ReconcileClosed(ResourceLedger.ResourceOwner owner, string adapterExecutable)
    {
        if (!Path.IsPathFullyQualified(adapterExecutable)) throw new ArgumentException("An absolute MCP adapter executable is required.");
        adapterExecutable = Path.GetFullPath(adapterExecutable);
        var released = new List<ResourceRecord>();
        foreach (var prior in owner.Ledger.Records().Where(r => r.AppId == owner.AppId && r.Kind == "mcp-adapter" && !r.Terminal))
        {
            var record = owner.ReconcileClosedMcpFrontend(prior.ResourceId, prior.Revision, adapterExecutable);
            if (record is not null) released.Add(record);
        }
        return released.ToArray();
    }
}
