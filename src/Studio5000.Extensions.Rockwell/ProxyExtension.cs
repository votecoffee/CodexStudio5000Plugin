using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Client;
using Studio5000.Extensibility;

namespace Studio5000.Extensions.Rockwell;

public abstract class ProxyExtension : IWorkspaceExtension
{
    protected void HmiLanguages(string prefix)
    {
        Get(prefix + "_languages_list", "List preserved offline HMI language workspaces. Platforms are explicit; filenames do not authenticate a target application.", _ => "/api/hmi-languages");
        Get(prefix + "_languages_get", "Read the exact language workspace revision/hash and verify managed bytes.", a => "/api/hmi-languages/" + E(a, "workspaceId"), F<string>("workspaceId"));
        Post<Studio5000.Core.HmiLanguageRegisterRequest>(prefix + "_languages_register", "apply", "Preserve a native ftview-me/ftview-se/view-designer language export. Detect OpenXML .xls/.xlsx by signature; Unicode .txt supported. FactoryTalk single-language text requires explicit Language. Legacy BIFF is rejected; no Studio/controller connection.", _ => "/api/hmi-languages");
        Post<Studio5000.Core.HmiLanguageQuery>(prefix + "_languages_browse", "read", "Search/page native string identities, context and all language strings at an exact source hash. No fallback or translation generation.", a => $"/api/hmi-languages/{E(a, "workspaceId")}/browse", F<string>("workspaceId"));
        Post<Studio5000.Core.HmiLanguagePreviewRequest>(prefix + "_languages_preview", "read", "Review up to 200 explicit translations and optional new language columns. Keep native identity/context/KEY columns, other languages and untouched workbook parts. Formulas/rich/protected cells are read-only.", a => $"/api/hmi-languages/{E(a, "workspaceId")}/preview", F<string>("workspaceId"));
        Post<Studio5000.Core.HmiLanguageApplyRequest>(prefix + "_languages_apply", "apply", "Recheck source and review hashes, independently read back every identity/string, and create an offline revision. Original native file preserved; never imports into Studio automatically.", a => $"/api/hmi-languages/{E(a, "workspaceId")}/apply", F<string>("workspaceId"));
        Post<Studio5000.Core.HmiLanguageExportRequest>(prefix + "_languages_export", "apply", "Create an immutable artifact in the original native language-file container. All existing language columns remain present. Save through studio_save_artifact.", a => $"/api/hmi-languages/{E(a, "workspaceId")}/export", F<string>("workspaceId"));
        Post<Studio5000.Core.HmiLanguageVerifyRequest>(prefix + "_languages_verify", "read", "Compare every native identity/language against a supplied re-export. Report missing/extra keys/languages and changed translations. Matching files alone do not establish native provenance, layout or runtime acceptance.", a => $"/api/hmi-languages/{E(a, "workspaceId")}/verify", F<string>("workspaceId"));
    }
    protected sealed record Field(string Name, Type Type, bool Optional = false);
    private readonly Dictionary<string, (ExtensionOperation Definition, Func<JsonElement, ServiceClient, CancellationToken, Task<JsonElement>> Invoke)> entries = new();
    protected static Field F<T>(string name, bool optional = false) => new(name, typeof(T), optional);
    protected static string S(JsonElement a, string key) => a.GetProperty(key).GetString()!;
    protected static string E(JsonElement a, string key) => Uri.EscapeDataString(S(a, key));
    protected static string Query(JsonElement a, string path, params string[] names)
    {
        var values = names.Where(n => a.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null)
            .Select(n => Uri.EscapeDataString(n) + "=" + Uri.EscapeDataString(a.GetProperty(n).ToString()));
        var query = string.Join("&", values); return path + (query.Length == 0 ? "" : "?" + query);
    }
    protected void Add(string name, string effect, string description, Field[] fields, Func<JsonElement, ServiceClient, CancellationToken, Task<JsonElement>> action)
    {
        var properties = new JsonObject(); foreach (var f in fields) properties[f.Name] = Schema(f.Type, []);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(fields.Where(f => !f.Optional).Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false };
        entries.Add(name, (new(name, description, effect, JsonSerializer.SerializeToElement(schema)), action));
    }
    protected void Get(string name, string description, Func<JsonElement, string> path, params Field[] fields) =>
        Add(name, "read", description, fields, (a, client, ct) => client.GetAsync<JsonElement>(path(a), ct));
    protected void Post<T>(string name, string effect, string description, Func<JsonElement, string> path, params Field[] fields) =>
        Add(name, effect, description, [..fields, F<T>("request")], (a, client, ct) => client.PostAsync<JsonElement>(path(a), a.GetProperty("request"), ct));
    protected void EmptyPost(string name, string effect, string description, Func<JsonElement, string> path, params Field[] fields) =>
        Add(name, effect, description, fields, (a, client, ct) => client.PostAsync<JsonElement>(path(a), new { }, ct));
    public ExtensionOperation[] Describe() => entries.Values.Select(x => x.Definition).ToArray();
    public async Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, ExtensionContext context, CancellationToken ct)
    {
        using var client = new ServiceClient(context.WorkspaceRoot);
        return await entries[operation].Invoke(arguments, client, ct);
    }
    private static JsonNode Schema(Type type, HashSet<Type> ancestors)
    {
        var nullable = Nullable.GetUnderlyingType(type); if (nullable is not null) return Schema(nullable, ancestors);
        if (type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type)) return new JsonObject();
        if (type == typeof(string) || type == typeof(DateTimeOffset) || type == typeof(DateTime)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type.IsPrimitive || type == typeof(decimal)) return new JsonObject { ["type"] = type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" : "integer" };
        if (type.IsArray) return new JsonObject { ["type"] = "array", ["items"] = Schema(type.GetElementType()!, ancestors) };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return new JsonObject { ["type"] = "object", ["additionalProperties"] = Schema(type.GenericTypeArguments[1], ancestors) };
        if (!ancestors.Add(type)) throw new ArgumentException("Recursive schema must use JsonElement: " + type.Name);
        try
        {
            var properties = new JsonObject(); var required = new JsonArray(); var nullability = new NullabilityInfoContext();
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            {
                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name); var schema = Schema(property.PropertyType, new(ancestors));
                var optional = nullability.Create(property).ReadState == NullabilityState.Nullable || Nullable.GetUnderlyingType(property.PropertyType) is not null;
                if (optional && schema["type"] is JsonValue kind) schema["type"] = new JsonArray(kind.GetValue<string>(), "null");
                properties[name] = schema;
                var parameter = constructor?.GetParameters().FirstOrDefault(p => p.Name!.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
                if (!optional && parameter?.HasDefaultValue != true) required.Add(name);
            }
            return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        }
        finally { ancestors.Remove(type); }
    }
}
