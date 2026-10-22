using System.Text.Json;

namespace Studio5000.Core.Resources;

public static class ManagedServiceAdmission
{
    public const string Profile = "owned-service-v2";
    public const string ShutdownBarrier = "Service shutdown requires generation-bound native close evidence.";
    public static bool ValidateReceipt(byte[] metadata, ResourceLedger ledger)
    {
        using var document = JsonDocument.Parse(metadata); var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2 || root.GetProperty("resourceProfile").GetString() != Profile) return false;
        var record = ledger.Get(root.GetProperty("resourceId").GetString()!);
        return record.AppId == "rockwell-mcp-server" && record.Process == ledger.Driver.CurrentIdentity().Normalize() &&
            record.OwnerId == root.GetProperty("ownerId").GetString() && record.Owner.Pid == root.GetProperty("pid").GetUInt32() &&
            record.Ownership == "private" && record.Containment == "windows_job" && record.State == "active" &&
            record.CleanupMode == "terminate" && record.Receipt is null && record.RecoveryBarrier == ShutdownBarrier && ledger.Driver.State(record.Owner) == "alive";
    }
    public static void WaitForOwner(string root, ResourceLedger ledger)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var path = Path.Combine(root, ".local", "management-supervisor.json");
        do
        {
            ProjectService.RejectLinks(path);
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[32769]; var count = 0;
                while (count < bytes.Length && file.Read(bytes, count, bytes.Length - count) is var read && read != 0) count += read;
                if (count == bytes.Length) throw new InvalidOperationException("Managed owner receipt exceeds its bound.");
                if (ValidateReceipt(bytes.AsSpan(0, count).ToArray(), ledger)) return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or ArgumentException) { /* Read-only startup wait; native admission stays closed. */ }
            Thread.Sleep(50);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new InvalidOperationException("Managed native admission requires the exact surviving supervisor and its retained shutdown barrier before initializing business services.");
    }
}
