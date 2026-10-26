using System.Diagnostics;
using System.Text.Json;

namespace Studio5000.Core;

public sealed record McpClientLimits(int MaximumInstances = 4, int ShutdownTimeoutSeconds = 10, int MaximumOwnedProcesses = 32, int MaximumOwnedResources = 32)
{
    public void Validate()
    {
        if (MaximumInstances is < 1 or > 16 || ShutdownTimeoutSeconds is < 1 or > 30)
            throw new ArgumentException("Use 1..16 MCP frontends and a shutdown timeout of 1..30 seconds.");
        OwnedProcessContainment.ValidateLimit(MaximumOwnedProcesses);
        if (MaximumOwnedResources is < 1 or > 256) throw new ArgumentException("MaximumOwnedResources must be between 1 and 256.");
    }

    public static McpClientLimits Read(string root)
    {
        var path = Path.Combine(Path.GetFullPath(root), "service.local.json");
        ProjectService.RejectLinks(path);
        if (!File.Exists(path)) return new();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[1024 * 1024 + 1]; var length = 0;
        while (length < bytes.Length && file.Read(bytes, length, bytes.Length - length) is var count && count != 0) length += count;
        if (length == bytes.Length) throw new ArgumentException("MCP settings exceed 1 MiB.");
        var start = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(start, length - start), new() { MaxDepth = 128 });
        var sections = document.RootElement.EnumerateObject().Where(p => p.Name.Equals("Studio5000", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sections.Length != 1 || sections[0].Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("One Studio5000 settings object is required.");
        int Number(string key, int fallback)
        {
            var matches = sections[0].Value.EnumerateObject().Where(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return fallback;
            if (matches.Length != 1 || matches[0].Value.ValueKind != JsonValueKind.Number || !matches[0].Value.TryGetInt32(out var value)) throw new ArgumentException("Invalid or duplicate MCP lifetime setting.");
            return value;
        }
        var result = new McpClientLimits(Number("MaximumMcpClients", 4), Number("McpShutdownTimeoutSeconds", 10), Number("MaximumOwnedProcesses", 32), Number("MaximumOwnedResources", 32));
        result.Validate(); return result;
    }
}

// OS file handles, not PID files or a semaphore count, own the slots. The OS
// releases them after an abrupt process exit. Legacy inventory is16; the new
// measured metadata profile has a separate explicit bound within64 shared slots.
public sealed class McpClientLease : IDisposable
{
    private FileStream? slot;
    public int Slot { get; }
    private McpClientLease(FileStream slot, int index) { this.slot = slot; Slot = index; }
    private static bool SharingViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33;

    public static McpClientLease Acquire(string root, McpClientLimits limits)
    {
        limits.Validate();
        return AcquirePool(root, "mcp-clients", limits.MaximumInstances, 16);
    }
    public static McpClientLease AcquireFrontdoor(string root, McpFrontdoorPolicy policy)
    {
        policy.Validate();
        return AcquirePool(root, "mcp-clients", policy.MaximumMetadataClients, McpFrontdoorPolicy.SlotInventory,
            active => policy.CheckReservation(active, McpFrontdoorPolicy.AvailableCommit()));
    }
    public static McpClientLease AcquireFrontdoorStart(string root, McpFrontdoorPolicy policy)
    {
        policy.Validate();
        return AcquirePool(root, "mcp-frontdoor-starts", policy.MaximumStartingClients, 8);
    }
    public static McpClientLease AcquireForwardedCall(string root, McpFrontdoorPolicy policy)
    {
        policy.Validate();
        return AcquirePool(root, "mcp-frontdoor-calls", policy.MaximumForwardedCalls, 16);
    }
    private static McpClientLease AcquirePool(string root, string pool, int maximum, int inventory, Action<int>? check = null)
    {
        var directory = Path.Combine(Path.GetFullPath(root), ".local", pool);
        ProjectService.RejectLinks(directory); Directory.CreateDirectory(directory); ProjectService.RejectLinks(directory);
        var admissionPath = Path.Combine(directory, "admission.lock"); ProjectService.RejectLinks(admissionPath);
        FileStream? admission = null;
        Resources.ResourceLedger.WaitForLock(() => admission = new(admissionPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), TimeSpan.FromSeconds(10));
        using (admission)
        {
            var active = 0; var available = -1;
            for (var index = 0; index < inventory; index++)
            {
                var path = Path.Combine(directory, $"slot-{index}.lock"); ProjectService.RejectLinks(path);
                try { using var probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); if (available < 0) available = index; }
                catch (IOException ex) when (SharingViolation(ex)) { active++; }
            }
            // Count all slots, including higher slots created before a limit was
            // lowered. Changing a limit never evicts or merges another client.
            if (active >= maximum || available < 0)
                throw new InvalidOperationException("mcp_client_limit: this installation has reached its configured frontend limit. Close an unused client before connecting; no existing client was stopped.");
            check?.Invoke(active);
            var stream = new FileStream(Path.Combine(directory, $"slot-{available}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                using var current = Process.GetCurrentProcess();
                var metadata = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, pid = current.Id, startedUtc = current.StartTime.ToUniversalTime(), maximumInstances = maximum });
                stream.SetLength(0); stream.Write(metadata); stream.Flush(true);
                return new(stream, available);
            }
            catch { stream.Dispose(); throw; }
        }
    }

    public void Dispose() => Interlocked.Exchange(ref slot, null)?.Dispose();
}
