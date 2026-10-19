using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Studio5000.Core;

public sealed record ServiceSettingsSnapshot(string Revision, JsonElement Saved, JsonElement Effective,
    bool RestartRequired, string[] ChangedSettings, string ConfigurationPath, string[] EnforcedChecks);
public sealed record ServiceSettingsSaveRequest(string ExpectedRevision, JsonElement Configuration);
public sealed record ServiceSettingsSaveResult(ServiceSettingsSnapshot Settings, string? BackupPath);

// One shared implementation for desktop/REST settings and offline staging. Saving never
// mutates the running service's options, opens a native engine or starts/stops polling.
public sealed class ServiceSettingsStore
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string root, path;
    private readonly JsonObject effective;
    private readonly object sync = new();
    private const int MaxBytes = 1024 * 1024;
    private static readonly string[] Checks = ["HTTP loopback and bearer authentication", "Browser-origin rejection",
        "Preserved sources and new export filenames", "Revision/hash and preview checks", "Schema and SDK argument validation",
        "Symbolic-link/junction rejection", "Auditing and indeterminate job receipts"];

    public ServiceSettingsStore(string root, string runningUrl, ServiceOptions runningOptions)
    {
        this.root = Path.GetFullPath(root); path = Path.Combine(this.root, "service.local.json");
        var document = Document(ReadBytes());
        document["ServiceUrl"] = runningUrl;
        document["Studio5000"] = JsonSerializer.SerializeToNode(runningOptions, Json);
        effective = Normalize(document, this.root);
    }
    public static ServiceSettingsStore OpenOffline(string root)
    {
        root = Path.GetFullPath(root);
        var path = Path.Combine(root, "service.local.json");
        var document = File.Exists(path) ? Parse(File.ReadAllBytes(path)) : Defaults();
        var options = Options(document);
        return new(root, Url(document), options);
    }
    public ServiceSettingsSnapshot Read() { lock (sync) return Snapshot(ReadBytes()); }
    public JsonElement BroadLocalPreview(JsonElement configuration)
    {
        var document = Parse(Encoding.UTF8.GetBytes(configuration.GetRawText())); _ = Normalize(document, root);
        var options = Options(document);
        options.EnableControllerOperations = true; options.AllowAnyControllerPath = true;
        options.AllowAnySourcePath = true; options.AllowAnyOutputPath = true;
        options.ControllerTargets = options.ControllerTargets.Select(t => t with { Policy = null }).ToArray();
        document["Studio5000"] = JsonSerializer.SerializeToNode(options, Json);
        _ = Normalize(document, root);
        return JsonSerializer.SerializeToElement(document, Json);
    }
    public ServiceSettingsSaveResult Save(ServiceSettingsSaveRequest request)
    {
        var document = Parse(Encoding.UTF8.GetBytes(request.Configuration.GetRawText())); _ = Normalize(document, root);
        var proposed = Encoding.UTF8.GetBytes(document.ToJsonString(Json));
        if (proposed.Length > MaxBytes) throw new ArgumentException("Settings exceed 1 MiB.");
        lock (sync)
        {
            var local = Path.Combine(root, ".local"); Directory.CreateDirectory(local); ProjectService.RejectLinks(local); ProjectService.RejectLinks(path);
            using var lease = new FileStream(Path.Combine(local, "settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var before = ReadBytes();
            if (Revision(before) != request.ExpectedRevision) throw new OperationException("settings_conflict", "Settings changed; reload and review before saving.");
            if (before is not null && before.AsSpan().SequenceEqual(proposed)) return new(Snapshot(before), null);
            string? backup = null;
            if (before is not null)
            {
                var history = Path.Combine(local, "settings-history"); Directory.CreateDirectory(history); ProjectService.RejectLinks(history);
                backup = Path.Combine(history, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllBytes(backup, before);
            }
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(proposed); file.Flush(true); }
                if (Revision(ReadBytes()) != request.ExpectedRevision) throw new OperationException("settings_conflict", "Settings changed while saving; reload and review.");
                for (var attempt = 0; ; attempt++)
                {
                    try { if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); break; }
                    catch (IOException) when (attempt < 7) { Thread.Sleep(25); }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(Snapshot(proposed), backup);
        }
    }
    private ServiceSettingsSnapshot Snapshot(byte[]? bytes)
    {
        var document = Document(bytes); var normalized = Normalize(document, root);
        var changed = normalized.Where(p => p.Key != "Studio5000" && !JsonNode.DeepEquals(p.Value, effective[p.Key])).Select(p => p.Key).ToList();
        foreach (var p in normalized["Studio5000"]!.AsObject())
            if (!JsonNode.DeepEquals(p.Value, effective["Studio5000"]![p.Key])) changed.Add("Studio5000." + p.Key);
        foreach (var key in effective.Select(p => p.Key).Except(normalized.Select(p => p.Key))) changed.Add(key);
        return new(Revision(bytes), JsonSerializer.SerializeToElement(document, Json), JsonSerializer.SerializeToElement(effective, Json),
            changed.Count != 0, changed.ToArray(), path, Checks.ToArray());
    }
    private byte[]? ReadBytes()
    {
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > MaxBytes) throw new ArgumentException("Settings exceed 1 MiB.");
        using var buffer = new MemoryStream(); file.CopyTo(buffer); return buffer.ToArray();
    }
    private static string Revision(byte[]? bytes) => bytes is null ? "missing" : ProjectService.Hash(bytes);
    private static JsonObject Document(byte[]? bytes) => bytes is null ? Defaults() : Parse(bytes);
    private static JsonObject Defaults() => new() { ["ServiceUrl"] = "http://127.0.0.1:5487", ["Studio5000"] = JsonSerializer.SerializeToNode(new ServiceOptions(), Json) };
    private static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new ArgumentException("Settings exceed 1 MiB.");
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        using var parsed = JsonDocument.Parse(text);
        void Check(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in item.EnumerateObject()) { if (!names.Add(p.Name)) throw new ArgumentException("Duplicate settings key: " + p.Name); Check(p.Value); }
            }
            else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Check(child);
        }
        Check(parsed.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("Settings must be a JSON object.");
    }
    private static string Url(JsonObject document) => document["ServiceUrl"]?.GetValue<string>() ?? throw new ArgumentException("ServiceUrl is required.");
    private static ServiceOptions Options(JsonObject document) => document["Studio5000"]?.Deserialize<ServiceOptions>(Json) ?? throw new ArgumentException("Studio5000 settings are required.");
    private static JsonObject Normalize(JsonObject document, string root)
    {
        var url = Url(document);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "http" || !IPAddress.TryParse(address.Host, out var ip) ||
            !IPAddress.IsLoopback(ip) || address.Port <= 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0)
            throw new ArgumentException("ServiceUrl must be a plain HTTP loopback IP endpoint.");
        var options = Options(document); ResolveAndValidate(options, root);
        var normalized = (JsonObject)document.DeepClone(); normalized["ServiceUrl"] = address.GetLeftPart(UriPartial.Authority);
        normalized["Studio5000"] = JsonSerializer.SerializeToNode(options, Json); return normalized;
    }
    public static void ResolveAndValidate(ServiceOptions options, string root)
    {
        string Full(string? value) => string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ? throw new ArgumentException("Configured paths must be nonempty and contain no control characters.") : Path.GetFullPath(value, root);
        options.StateDirectory = Full(options.StateDirectory);
        if (options.AllowedRoots is null || options.OutputRoots is null || options.AllowedRoots.Length > 512 || options.OutputRoots.Length > 512 ||
            options.AllowedRoots.Length == 0 && !options.AllowAnySourcePath) throw new ArgumentException("Use at most 512 roots; restricted source access requires a root.");
        options.AllowedRoots = options.AllowedRoots.Select(Full).ToArray(); options.OutputRoots = options.OutputRoots.Select(Full).ToArray();
        // ConfigurationBinder represents JSON null strings as empty on some hosts.
        options.NativeBridgePath = string.IsNullOrWhiteSpace(options.NativeBridgePath) ? null : Full(options.NativeBridgePath);
        options.CipBridgePath = string.IsNullOrWhiteSpace(options.CipBridgePath) ? null : Full(options.CipBridgePath);
        options.FactoryTalkSchemaDirectory = Full(options.FactoryTalkSchemaDirectory); options.FactoryTalkStudioPath = Full(options.FactoryTalkStudioPath);
        options.ViewDesignerPath = Full(options.ViewDesignerPath); options.ExtensionsDirectory = Full(options.ExtensionsDirectory); options.ExtensionWorkerPath = Full(options.ExtensionWorkerPath);
        if (options.NativeTimeoutSeconds is < 1 or > 86400 || options.ExtensionTimeoutSeconds is < 1 or > 1800 ||
            options.MaximumConcurrentMutations is < 1 or > 32 || options.MaximumQueuedMutations is < 1 or > 4096)
            throw new ArgumentException("Use native timeout 1..86400 s, extension timeout 1..1800 s, active mutations 1..32 and queue 1..4096.");
        if (options.ControllerTargets is null || options.ControllerTargets.Length > 256 ||
            options.ControllerTargets.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.CommunicationsPath) || t.Id.Any(char.IsControl) || t.CommunicationsPath.Any(char.IsControl)) ||
            options.ControllerTargets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != options.ControllerTargets.Length)
            throw new ArgumentException("Use at most 256 controller targets with unique nonempty IDs and communications paths.");
        foreach (var target in options.ControllerTargets) { SdkTargetPolicies.ValidateConfiguration(target); if (target.CipRead is not null) CipWatchPlanning.ValidateTarget(target.CipRead); }
    }
}
