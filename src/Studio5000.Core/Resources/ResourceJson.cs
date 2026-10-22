using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Studio5000.Core.Resources;

// Matches universal_tools.models.canonical_digest: sorted keys, ensure_ascii,
// compact separators and Python float representation. Never hashes input bytes
// whose optional defaults/identity normalization differ from the runtime model.
public static class ResourceJson
{
    private static readonly JsonSerializerOptions Wire = new() { MaxDepth = 32 };
    private static readonly string[] Required = ["schema_version", "resource_id", "app_id", "owner_id", "owner", "label", "ownership", "state", "created_at", "updated_at"];
    public static byte[] Write(ResourceRecord record) => Encoding.UTF8.GetBytes(Canonical(JsonSerializer.SerializeToElement(record.Normalize(), Wire)) + "\n");
    public static ResourceRecord Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 32768) throw new ArgumentException("Resource record exceeds 32 KiB.");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Required.Any(k => !root.TryGetProperty(k, out _))) throw new ArgumentException("Missing required resource field.");
        CheckDuplicates(root);
        if (root.GetProperty("schema_version").GetRawText() != "1") throw new ArgumentException("Resource schema_version must be integer 1.");
        return root.Deserialize<ResourceRecord>(Wire)!.Normalize();
    }
    public static string Digest(ResourceRecord record) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(JsonSerializer.SerializeToElement(record.Normalize(), Wire))))).ToLowerInvariant();
    public static string Canonical(JsonElement value)
    {
        var output = new StringBuilder(); Append(value, output); return output.ToString();
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!keys.Add(p.Name)) throw new ArgumentException("Duplicate resource JSON field."); CheckDuplicates(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
    private static void Append(JsonElement value, StringBuilder output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{'); var first = true;
                foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) output.Append(','); first = false; String(p.Name, output); output.Append(':');
                    if (p.Name is "created_at" or "updated_at" or "recorded_at" && p.Value.ValueKind == JsonValueKind.Number)
                        output.Append(PythonFloat(p.Value.GetDouble()));
                    else Append(p.Value, output);
                }
                output.Append('}'); break;
            case JsonValueKind.Array:
                output.Append('['); first = true;
                foreach (var item in value.EnumerateArray()) { if (!first) output.Append(','); first = false; Append(item, output); }
                output.Append(']'); break;
            case JsonValueKind.String: String(value.GetString()!, output); break;
            case JsonValueKind.Number:
                output.Append(value.GetRawText()); break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new ArgumentException("Unsupported resource JSON value.");
        }
    }
    private static string PythonFloat(double value)
    {
        ResourceRules.Finite(value);
        var text = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var negative = text.StartsWith('-'); var unsigned = negative ? text[1..] : text;
        var parts = unsigned.Split('e'); var mantissa = parts[0];
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.'); if (dot < 0) dot = mantissa.Length;
        var digits = mantissa.Replace(".", ""); var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0') leading++;
        var scientificExponent = exponent + dot - leading - 1;
        digits = digits[leading..].TrimEnd('0'); if (digits.Length == 0) digits = "0";
        var prefix = negative ? "-" : "";
        if (value != 0 && (Math.Abs(value) >= 1e16 || Math.Abs(value) < 1e-4))
            return prefix + digits[0] + (digits.Length == 1 ? "" : "." + digits[1..]) + "e" +
                (scientificExponent < 0 ? "-" : "+") + Math.Abs(scientificExponent).ToString("D2", CultureInfo.InvariantCulture);
        if (parts.Length == 1) return text.Contains('.') ? text : text + ".0";
        var position = scientificExponent + 1;
        var fixedText = position <= 0 ? "0." + new string('0', -position) + digits : position >= digits.Length ? digits + new string('0', position - digits.Length) + ".0" : digits[..position] + "." + digits[position..];
        return prefix + fixedText;
    }
    private static void String(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var c in value)
            switch (c)
            {
                case '"': output.Append("\\\""); break; case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break; case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break; case '\r': output.Append("\\r"); break; case '\t': output.Append("\\t"); break;
                default: if (c < 32 || c > 126) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else output.Append(c); break;
            }
        output.Append('"');
    }
}
