using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OfflinePinnedFile(string Path, long Size, string Sha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OfflineFrontendPin(string Directory, OfflinePinnedFile[] Files, string? UnboundLaunchContract = null,
    string? SourceCommit = null, string? BackendManifestSha256 = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OfflineServicePin(int SchemaVersion, string ServiceVersion, OfflinePinnedFile[] Files, OfflineFrontendPin[] PreviousFrontends)
{
    internal const string FileName = "offline-service.json";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    internal const string Service = "artifacts/app/Service/Studio5000.Service.exe";
    internal const string Worker = "artifacts/app/ExtensionWorker/Studio5000.ExtensionWorker.exe";
    internal const string Script = "scripts/Start-Service.ps1";
    internal const string LegacyUnboundContract = "windows-private-job-bind-before-resume-v1";
    internal const string LegacyUnboundSource = "31a57922bde082f82567ebcc035b29ab0a35d9da";
    internal static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    internal static JsonDocument Read(string path, int maximum)
        => Read(path, maximum, out _);
    internal static JsonDocument Read(string path, int maximum, out string revision)
    {
        ProjectService.RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maximum) throw new InvalidOperationException("Offline bootstrap metadata exceeds its bound.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        revision = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var start = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var document = JsonDocument.Parse(bytes.AsMemory(start), new() { MaxDepth = 64 });
        try { CheckDuplicates(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate offline bootstrap metadata field."); CheckDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
    internal static OfflineServicePin Load(string root, string backend)
    {
        using var document = Read(System.IO.Path.Combine(backend, FileName), 262144);
        var pin = document.Deserialize<OfflineServicePin>(Json) ?? throw new InvalidOperationException("Missing offline service pin.");
        if (pin.SchemaVersion != 1 || !System.Text.RegularExpressions.Regex.IsMatch(pin.ServiceVersion ?? "", @"^\d+\.\d+\.\d+$") || pin.PreviousFrontends is null || pin.PreviousFrontends.Length > 8)
            throw new InvalidOperationException("Unsupported offline service pin.");
        VerifyFiles(root, pin.Files, [Service, "artifacts/app/Service/Studio5000.Service.dll", "artifacts/app/Service/Studio5000.Core.dll", Worker,
            "artifacts/app/ExtensionWorker/Studio5000.ExtensionWorker.dll", Script]);
        if (pin.Files.Any(f => f.Path != Script && !f.Path.StartsWith("artifacts/app/Service/", StringComparison.Ordinal) && !f.Path.StartsWith("artifacts/app/ExtensionWorker/", StringComparison.Ordinal)))
            throw new InvalidOperationException("Pin contains files outside the selected service/worker closure.");
        var expected = pin.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var prefix in new[] { "artifacts/app/Service", "artifacts/app/ExtensionWorker" })
            foreach (var file in System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(root, prefix), "*", SearchOption.AllDirectories))
                if (!expected.Contains(System.IO.Path.GetRelativePath(root, file).Replace('\\', '/'))) throw new InvalidOperationException("Selected service has an extra unpinned file.");
        foreach (var previous in pin.PreviousFrontends)
        {
            if (!System.IO.Path.IsPathFullyQualified(previous.Directory)) throw new InvalidOperationException("Predecessor closure must be explicit.");
            VerifyFiles(previous.Directory, previous.Files, ["Studio5000.StdioHost.exe", "Studio5000.Mcp.exe", "Studio5000.Core.dll"]);
            if (previous.UnboundLaunchContract is not null)
            {
                if (previous.UnboundLaunchContract != LegacyUnboundContract || previous.SourceCommit != LegacyUnboundSource ||
                    !System.Text.RegularExpressions.Regex.IsMatch(previous.BackendManifestSha256 ?? "", "^[a-f0-9]{64}$"))
                    throw new InvalidOperationException("Unbound retirement requires the explicitly reviewed legacy stateless launch contract.");
                var manifestPath = System.IO.Path.Combine(previous.Directory, "backend.json"); ProjectService.RejectLinks(manifestPath);
                if (Hash(manifestPath) != previous.BackendManifestSha256) throw new InvalidOperationException("Legacy provenance changed after review.");
                using var manifest = Read(manifestPath, 262144);
                if (manifest.RootElement.GetProperty("source_commit").GetString() != previous.SourceCommit ||
                    manifest.RootElement.GetProperty("contract").GetString() != "rockwell-owned-stdio-v1")
                    throw new InvalidOperationException("Legacy source provenance does not match the reviewed stateless contract.");
                var original = manifest.RootElement.GetProperty("files").Deserialize<OfflinePinnedFile[]>(Json)!;
                if (original.Length != previous.Files.Length || original.Any(f => !previous.Files.Contains(f)))
                    throw new InvalidOperationException("Legacy closure differs from its immutable publication inventory.");
                VerifyFiles(previous.Directory, previous.Files, ["Studio5000.StdioHost.exe", "Studio5000.StdioHost.dll", "Studio5000.StdioHost.deps.json", "Studio5000.StdioHost.runtimeconfig.json",
                    "Studio5000.Mcp.exe", "Studio5000.Mcp.dll", "Studio5000.Mcp.deps.json", "Studio5000.Mcp.runtimeconfig.json", "Studio5000.Core.dll"]);
            }
        }
        return pin;
    }
    internal static string ProofHash(OfflineFrontendPin previous) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(previous, Json))).ToLowerInvariant();
    internal static void VerifyFiles(string root, OfflinePinnedFile[] files, string[] required)
    {
        if (files is null || files.Length is < 1 or > 256 || files.Sum(f => f.Size) > 268435456) throw new InvalidOperationException("Offline pin inventory exceeds its bound.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files)
        {
            if (string.IsNullOrEmpty(entry.Path) || entry.Path.Contains('\\') || entry.Path.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':')) ||
                !seen.Add(entry.Path) || entry.Size < 0 || !System.Text.RegularExpressions.Regex.IsMatch(entry.Sha256 ?? "", "^[a-f0-9]{64}$")) throw new InvalidOperationException("Invalid offline pin member.");
            var path = System.IO.Path.Combine(root, entry.Path); ProjectService.RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Size || Hash(path) != entry.Sha256) throw new InvalidOperationException("Selected offline runtime or starter changed after review.");
        }
        if (required.Any(path => !files.Any(f => f.Path == path))) throw new InvalidOperationException("Offline runtime pin is incomplete.");
    }
    internal static OfflineBootstrapSettings Settings(string root)
    {
        var path = System.IO.Path.Combine(root, "service.local.json");
        using var document = Read(path, 1048576, out var revision);
        return new(ValidateSettings(root, document.RootElement), revision);
    }
    internal static Uri ValidateSettings(string root, JsonElement configuration)
    {
        CheckDuplicates(configuration);
        var properties = configuration.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (!properties.TryGetValue("ServiceUrl", out var rawUrl) || !Uri.TryCreate(rawUrl.GetString(), UriKind.Absolute, out var url) || url.Scheme != "http" ||
            !IPAddress.TryParse(url.Host, out var ip) || !IPAddress.IsLoopback(ip) || url.AbsolutePath != "/" || url.Query != "" || url.Fragment != "" || url.UserInfo != "") throw new InvalidOperationException("Offline startup requires a loopback HTTP service URL.");
        var options = properties["Studio5000"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "EnableControllerOperations", "EnableSdkServiceRecovery", "AutoStartSdkService" })
            if (!options.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.False) throw new InvalidOperationException("Automatic startup requires explicitly disabled controller/SDK/SCM operations.");
        foreach (var name in new[] { "NativeBridgePath", "CipBridgePath" })
            if (!options.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Null) throw new InvalidOperationException("Automatic startup requires native SDK/CIP bridges disabled.");
        foreach (var name in new[] { "FactoryTalkStudioPath", "ViewDesignerPath" })
            if (options.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && File.Exists(System.IO.Path.GetFullPath(value.GetString()!, root)))
                throw new InvalidOperationException("Native HMI launchers must be disabled for automatic offline startup.");
        if (!options.TryGetValue("ExtensionWorkerPath", out var worker) || worker.ValueKind != JsonValueKind.String ||
            !string.Equals(System.IO.Path.GetFullPath(worker.GetString()!, root), System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Worker)), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Offline startup requires the pinned extension worker.");
        if (File.Exists(System.IO.Path.Combine(root, ".local", "management-runtime.json"))) throw new InvalidOperationException("Automatic offline startup cannot migrate a selected management host.");
        return url;
    }
}
internal sealed record OfflineBootstrapSettings(Uri Url, string SettingsSha256);
