using System.Diagnostics;
using System.Text.Json;

namespace Studio5000.Core;

public sealed record McpClientLimits(int MaximumInstances = 4, int ShutdownTimeoutSeconds = 10)
{
    public void Validate()
    {
        if (MaximumInstances is < 1 or > 16 || ShutdownTimeoutSeconds is < 1 or > 30)
            throw new ArgumentException("Use 1..16 MCP frontends and a shutdown timeout of 1..30 seconds.");
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
        var result = new McpClientLimits(Number("MaximumMcpClients", 4), Number("McpShutdownTimeoutSeconds", 10));
        result.Validate(); return result;
    }
}

// OS file handles, not PID files or a semaphore count, own the slots. The OS
// releases them after an abrupt process exit. Only 16 slots and one lock exist.
public sealed class McpClientLease : IDisposable
{
    private FileStream? slot;
    public int Slot { get; }
    private McpClientLease(FileStream slot, int index) { this.slot = slot; Slot = index; }
    private static bool SharingViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33;

    public static McpClientLease Acquire(string root, McpClientLimits limits)
    {
        limits.Validate();
        var directory = Path.Combine(Path.GetFullPath(root), ".local", "mcp-clients");
        ProjectService.RejectLinks(directory); Directory.CreateDirectory(directory); ProjectService.RejectLinks(directory);
        var admissionPath = Path.Combine(directory, "admission.lock"); ProjectService.RejectLinks(admissionPath);
        FileStream? admission = null;
        for (var attempt = 0; admission is null; attempt++)
        {
            try { admission = new(admissionPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when (SharingViolation(ex) && attempt < 20) { Thread.Sleep(10); }
        }
        using (admission)
        {
            var active = 0; var available = -1;
            for (var index = 0; index < 16; index++)
            {
                var path = Path.Combine(directory, $"slot-{index}.lock"); ProjectService.RejectLinks(path);
                try { using var probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); if (available < 0) available = index; }
                catch (IOException ex) when (SharingViolation(ex)) { active++; }
            }
            // Count all slots, including higher slots created before a limit was
            // lowered. Changing a limit never evicts or merges another client.
            if (active >= limits.MaximumInstances || available < 0)
                throw new InvalidOperationException("mcp_client_limit: this installation has reached its configured frontend limit. Close an unused client before connecting; no existing client was stopped.");
            var stream = new FileStream(Path.Combine(directory, $"slot-{available}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                using var current = Process.GetCurrentProcess();
                var metadata = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, pid = current.Id, startedUtc = current.StartTime.ToUniversalTime(), maximumInstances = limits.MaximumInstances });
                stream.SetLength(0); stream.Write(metadata); stream.Flush(true);
                return new(stream, available);
            }
            catch { stream.Dispose(); throw; }
        }
    }

    public void Dispose() => Interlocked.Exchange(ref slot, null)?.Dispose();
}
