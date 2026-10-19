using System.Text.Json;

namespace Studio5000.Extensibility;

public static class ExtensionProtocol
{
    public const int Version = 1;
    public const int MaxReplyCharacters = 12 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    public static readonly string[] Effects = ["read", "preview", "apply", "control"];
}
public sealed record ExtensionOperation(string Name, string Description, string Effect, JsonElement InputSchema);
public sealed record ExtensionContext(string PackageDirectory, string WorkspaceRoot);
// Trusted local code only. Workers provide process/lifetime isolation, not a security sandbox.
public interface IWorkspaceExtension
{
    ExtensionOperation[] Describe();
    Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, ExtensionContext context, CancellationToken ct);
}
public sealed record ExtensionPackage(string Id, string Version, int ContractVersion, string EntryAssembly,
    string EntryType, Dictionary<string, string> Files, int ParallelWorkers = 1);
public sealed record ExtensionPointer(string Version);
public sealed record ExtensionSummary(string Id, string Version, string Generation, string PackageSha256,
    ExtensionOperation[] Operations, int ActiveCalls, string State, int ParallelWorkers = 1);
public sealed record ExtensionCatalog(int ProtocolVersion, ExtensionSummary[] Extensions, string[] LoadErrors);
public sealed record ExtensionReloadRequest(string ExtensionId, string? Version = null, string? ExpectedGeneration = null);
public sealed record ExtensionCall(string ExtensionId, string Operation, string Generation, JsonElement Arguments);
public sealed record ExtensionJobRequest(string Effect, ExtensionCall Call);
public sealed record ExtensionJob(string Id, string ExtensionId, string Operation, string Generation, string Effect,
    string Status, DateTimeOffset CreatedAt, JsonElement? Result = null, string? Error = null);
public sealed record WorkerRequest(string Method, string? Operation = null, JsonElement? Arguments = null);
public sealed record WorkerReply(bool Success, JsonElement? Value = null, string? Error = null);

// Deliberately bounded schema vocabulary. Unknown keywords are rejected, never silently ignored.
public static class ExtensionSchema
{
    private static readonly HashSet<string> Keywords = ["type", "properties", "required", "additionalProperties", "items", "enum", "description"];
    public static void CheckDefinition(JsonElement schema, int depth = 0)
    {
        if (depth > 24 || schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid extension schema depth/object.");
        foreach (var p in schema.EnumerateObject())
        {
            if (!Keywords.Contains(p.Name)) throw new ArgumentException("Unsupported schema keyword: " + p.Name);
            switch (p.Name)
            {
                case "type":
                    var types = p.Value.ValueKind == JsonValueKind.String ? new[] { p.Value.GetString() } : p.Value.EnumerateArray().Select(x => x.GetString()).ToArray();
                    if (types.Length == 0 || types.Any(x => x is not ("object" or "array" or "string" or "number" or "integer" or "boolean" or "null"))) throw new ArgumentException("Invalid schema type.");
                    break;
                case "properties": foreach (var child in p.Value.EnumerateObject()) CheckDefinition(child.Value, depth + 1); break;
                case "items": CheckDefinition(p.Value, depth + 1); break;
                case "additionalProperties": if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) CheckDefinition(p.Value, depth + 1); break;
                case "required": foreach (var child in p.Value.EnumerateArray()) if (child.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid required property."); break;
                case "enum": if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() == 0) throw new ArgumentException("Invalid enum."); break;
                case "description": if (p.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid description."); break;
            }
        }
    }
    public static void Validate(JsonElement schema, JsonElement value, string path = "$", int depth = 0)
    {
        if (depth > 24) throw new ArgumentException("Arguments exceed schema depth.");
        if (schema.TryGetProperty("type", out var type))
        {
            bool Matches(string? t) => t switch
            {
                "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String, "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False, "null" => value.ValueKind == JsonValueKind.Null, _ => false
            };
            if (type.ValueKind == JsonValueKind.String ? !Matches(type.GetString()) : !type.EnumerateArray().Any(t => Matches(t.GetString())))
                throw new ArgumentException(path + " has the wrong JSON type.");
        }
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(x => x.GetRawText() == value.GetRawText())) throw new ArgumentException(path + " is outside the enum.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required)) foreach (var key in required.EnumerateArray())
                if (!value.TryGetProperty(key.GetString()!, out _)) throw new ArgumentException(path + " requires " + key.GetString());
            var hasProps = schema.TryGetProperty("properties", out var props);
            foreach (var p in value.EnumerateObject())
            {
                if (hasProps && props.TryGetProperty(p.Name, out var child)) Validate(child, p.Value, path + "." + p.Name, depth + 1);
                else if (schema.TryGetProperty("additionalProperties", out var extra))
                {
                    if (extra.ValueKind == JsonValueKind.False) throw new ArgumentException(path + " has unknown property " + p.Name);
                    if (extra.ValueKind == JsonValueKind.Object) Validate(extra, p.Value, path + "." + p.Name, depth + 1);
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items)) foreach (var item in value.EnumerateArray()) Validate(items, item, path + "[]", depth + 1);
    }
}
