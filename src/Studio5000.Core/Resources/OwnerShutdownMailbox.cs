using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio5000.Core.Resources;

public sealed record OwnerShutdownDecision(bool Allowed, string Message);

// Private generation-bound operator IPC, not a public MCP or manager restart
// route. Domain quiescence/save/refusal is supplied by the shared service.
public sealed class OwnerShutdownMailbox(string root, string ownerId, string key, Action<string, string>? replaceFile = null)
{
    private readonly SemaphoreSlim poll = new(1, 1);
    private readonly Dictionary<string, object> delivered = [];
    private string? consoleRequestId;
    public void RequestOnce()
    {
        lock (delivered)
        {
            if (consoleRequestId is not null) return;
            var path = Path.Combine(root, ".local", "service-owner-stop.json"); ProjectService.RejectLinks(path);
            if (File.Exists(path))
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 8192) throw new IOException("Existing owner stop intent requires inspection.");
                var prior = JsonSerializer.Deserialize<StopIntent>(file);
                if (prior?.OwnerId == ownerId && prior.ResourceId == ownerId) { ResourceRules.Token(prior.RequestId, 32); consoleRequestId = prior.RequestId; return; }
            }
            consoleRequestId = Guid.NewGuid().ToString("N");
            var body = "rockwell-stop-v1\n" + ownerId + "\n" + ownerId + "\n" + consoleRequestId;
            var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            NativeLifecycleJournal.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new StopIntent(1, ownerId, ownerId, consoleRequestId, signature)));
        }
    }
    public async Task<bool> PollAsync(Func<Task<OwnerShutdownDecision>> closeOwnedResources, CancellationToken ct)
    {
        await poll.WaitAsync(ct);
        try { return await PollCoreAsync(closeOwnedResources, ct); }
        finally { poll.Release(); }
    }
    private async Task<bool> PollCoreAsync(Func<Task<OwnerShutdownDecision>> closeOwnedResources, CancellationToken ct)
    {
        var path = Path.Combine(root, ".local", "service-owner-stop.json"); ProjectService.RejectLinks(path);
        if (!File.Exists(path)) return false;
        byte[] bytes;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[8193]; var length = 0;
            while (length < bytes.Length && input.Read(bytes, length, bytes.Length - length) is var count && count != 0) length += count;
            if (length == bytes.Length) return false; Array.Resize(ref bytes, length);
        }
        StopIntent? request;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count()) return false;
            request = JsonSerializer.Deserialize<StopIntent>(bytes);
        }
        catch (JsonException) { return false; }
        if (request is null || request.SchemaVersion != 1 || request.OwnerId != ownerId || request.ResourceId != ownerId || key.Length < 32) return false;
        try { ResourceRules.Token(request.RequestId, 32); ResourceRules.Token(request.Signature, 64); }
        catch (ArgumentException) { return false; }
        var body = "rockwell-stop-v1\n" + ownerId + "\n" + ownerId + "\n" + request.RequestId;
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(request.Signature))) return false;
        var retained = Path.Combine(root, ".local", "owner-stop-history", request.RequestId + ".json"); ProjectService.RejectLinks(retained);
        if (File.Exists(retained)) return false; // Never redispatch a retained intent after object/host recreation.
        if (delivered.ContainsKey(request.RequestId) || delivered.Count >= 256) return false;
        ct.ThrowIfCancellationRequested();
        object Result(string state, string message) => new { schemaVersion = 1, ownerId, requestId = request.RequestId, state, message };
        var intent = Result("reviewing", "Checking app-owned quiescence and native close/refusal policy");
        // FileMode.CreateNew claims the intent across mailbox object instances.
        // Once durable, even a crash before dispatch requires inspection.
        if (!Persist(intent, claim: true)) return false;
        delivered[request.RequestId] = intent;
        OwnerShutdownDecision decision;
        try { decision = await closeOwnedResources(); }
        catch (Exception)
        {
            var uncertain = Result("uncertain", "Shutdown dispatch outcome is uncertain; inspect retained resource state and do not replay.");
            Persist(uncertain); delivered[request.RequestId] = uncertain; return false;
        }
        var result = Result(decision.Allowed ? "completed" : "blocked", decision.Message);
        Persist(result); delivered[request.RequestId] = result;
        return decision.Allowed;
    }
    private bool Persist(object response, bool claim = false)
    {
        var path = Path.Combine(root, ".local", "service-owner-stop-result.json"); ProjectService.RejectLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        using var document = JsonDocument.Parse(bytes); var id = document.RootElement.GetProperty("requestId").GetString()!;
        var history = Path.Combine(root, ".local", "owner-stop-history"); ProjectService.RejectLinks(history); Directory.CreateDirectory(history);
        var record = Path.Combine(history, id + ".json"); ProjectService.RejectLinks(record);
        if (!File.Exists(record) && Directory.EnumerateFiles(history, "*.json").Take(4096).Count() >= 4096)
            throw new InvalidOperationException("Stop intent history is full; preserve/archive evidence before admitting another request.");
        if (claim)
        {
            try { using var file = new FileStream(record, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true); }
            catch (IOException) when (File.Exists(record)) { return false; }
        }
        foreach (var target in claim ? new[] { path } : new[] { record, path })
        {
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
                if (File.Exists(target)) ReplaceReceipt(temp, target); else File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return true;
    }
    private void ReplaceReceipt(string temporary, string target)
    {
        AtomicMetadataReplacement.Replace(temporary, target, replaceFile);
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StopIntent(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("ownerId")] string OwnerId,
        [property: JsonPropertyName("resourceId")] string ResourceId,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("signature")] string Signature);
}
