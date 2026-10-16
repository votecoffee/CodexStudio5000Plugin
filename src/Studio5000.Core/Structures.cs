using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record StructureMember(string Name, string DataType, int[] Dimensions, bool Hidden, string? Target, int? BitNumber, string? ExternalAccess);
public sealed record StructureType(string Name, string? Family, StructureMember[] Members);
public sealed record StructuredTag(string Name, string? ProgramName, string DataType, int[] Dimensions, JsonNode? Value, string Quality, string? Error = null);
public sealed record StructureExtraction(string ProjectId, string SourceSha256, string Origin, DateTimeOffset? UploadedAt,
    StructureType[] Types, StructuredTag[] Tags, string Evidence);
public sealed record StructureExtractRequest(string? ProjectId = null, string? JobId = null, string? DataTypeName = null,
    string? TagName = null, string? ProgramName = null, bool IncludeValues = true);
public sealed record StructurePullRequest(string ControllerTargetId);

// Uses the SDK's decorated XML representation, never inferred controller memory offsets.
public static class LogixStructures
{
    public static int[] Dimensions(string? text) => string.IsNullOrWhiteSpace(text) || text == "0" ? [] :
        text.Trim('[', ']').Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(v =>
            int.TryParse(v, out var n) && n > 0 ? n : throw new OperationException("structure_dimensions", "Invalid array dimensions.")).ToArray();

    public static StructureType[] Types(XDocument doc) => doc.Root!.Element("Controller")!.Elements("DataTypes").Elements("DataType")
        .Select(t => new StructureType((string)t.Attribute("Name")!, (string?)t.Attribute("Family"),
            t.Elements("Members").Elements("Member").Select(m => new StructureMember((string)m.Attribute("Name")!,
                (string)m.Attribute("DataType")!, Dimensions((string?)m.Attribute("Dimension")), (string?)m.Attribute("Hidden") == "true",
                (string?)m.Attribute("Target"), (int?)m.Attribute("BitNumber"), (string?)m.Attribute("ExternalAccess"))).ToArray())).ToArray();

    public static XElement Tag(XDocument doc, string name, string? program)
    {
        WatchValidation.Identifier(name); if (program is not null) WatchValidation.Identifier(program);
        var controller = doc.Root!.Element("Controller")!;
        var parent = program is null ? controller : controller.Elements("Programs").Elements("Program").SingleOrDefault(p => (string?)p.Attribute("Name") == program);
        return parent?.Elements("Tags").Elements("Tag").SingleOrDefault(t => (string?)t.Attribute("Name") == name)
            ?? throw new OperationException("structure_tag_missing", "The exact tag was not found in this project's metadata.");
    }

    public static StructuredTag ExtractTag(XElement tag, string? program, bool values = true)
    {
        var name = (string)tag.Attribute("Name")!; var type = (string?)tag.Attribute("DataType") ?? "Unknown";
        var dims = Dimensions((string?)tag.Attribute("Dimensions"));
        if (!values) return new(name, program, type, dims, null, "schema_only");
        try
        {
            var data = tag.Elements("Data").FirstOrDefault(d => (string?)d.Attribute("Format") == "Decorated");
            if (data?.Elements().SingleOrDefault() is not { } value)
                return new(name, program, type, dims, null, "unavailable", "No decorated value is present; saved data is not a live read.");
            var budget = 10000;
            return new(name, program, type, dims, Decode(value, ref budget), "snapshot");
        }
        catch (Exception ex) when (ex is OperationException or FormatException or OverflowException or InvalidOperationException)
        { return new(name, program, type, dims, null, "bad", ex.Message); }
    }

    private static JsonNode? Decode(XElement value, ref int budget, int depth = 0)
    {
        if (--budget < 0 || depth > 64) throw new OperationException("structure_too_large", "A structured value exceeds the node/depth limit.");
        if (value.Name.LocalName is "Structure" or "StructureMember")
        {
            var result = new JsonObject();
            foreach (var member in value.Elements()) result.Add((string?)member.Attribute("Name") ?? throw new FormatException("Unnamed member."), Decode(member, ref budget, depth + 1));
            return result;
        }
        if (value.Name.LocalName is "Array" or "ArrayMember")
        {
            var dims = Dimensions((string?)value.Attribute("Dimensions"));
            if (dims.Length == 0 || dims.Length > 3 || dims.Aggregate(1L, (a, b) => checked(a * b)) > budget)
                throw new OperationException("structure_too_large", "Array dimensions exceed the extraction limit.");
            var elements = value.Elements("Element").ToDictionary(e => (string)e.Attribute("Index")!);
            JsonArray Build(int level, int[] indices)
            {
                var array = new JsonArray();
                for (var i = 0; i < dims[level]; i++)
                {
                    indices[level] = i;
                    if (level + 1 < dims.Length) array.Add(Build(level + 1, indices));
                    else
                    {
                        var key = "[" + string.Join(',', indices) + "]";
                        if (!elements.TryGetValue(key, out var element)) throw new FormatException("Missing array element " + key);
                        array.Add(element.Elements().SingleOrDefault() is { } child ? DecodeLocal(child) : Scalar((string?)value.Attribute("DataType"), (string?)element.Attribute("Value") ?? ""));
                    }
                }
                return array;
            }
            // A local counter avoids capturing a ref parameter from the recursive array builder.
            var remaining = budget;
            JsonNode? DecodeLocal(XElement element) => Decode(element, ref remaining, depth + 1);
            var output = Build(0, new int[dims.Length]); budget = remaining - elements.Count; return output;
        }
        return Scalar((string?)value.Attribute("DataType"), (string?)value.Attribute("Value") ?? value.Value);
    }

    private static JsonNode Scalar(string? type, string text)
    {
        if (type == "BOOL" || type == "BIT") return text switch { "1" or "true" or "TRUE" => JsonValue.Create(true), "0" or "false" or "FALSE" => JsonValue.Create(false), _ => throw new FormatException("Invalid BOOL value.") };
        if (type is "REAL" or "LREAL") return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? JsonValue.Create(number) : JsonValue.Create(text);
        if (type is "SINT" or "INT" or "DINT" or "LINT" or "USINT" or "UINT" or "UDINT" or "ULINT")
        {
            var plain = text.Replace("_", "");
            if (plain.Contains('#'))
            {
                var parts = plain.Split('#'); var raw = Convert.ToUInt64(parts[1], int.Parse(parts[0], CultureInfo.InvariantCulture));
                return type switch { "SINT" => JsonValue.Create(unchecked((sbyte)raw)), "INT" => JsonValue.Create(unchecked((short)raw)),
                    "DINT" => JsonValue.Create(unchecked((int)raw)), "LINT" => JsonValue.Create(unchecked((long)raw)), _ => JsonValue.Create(raw) };
            }
            return type.StartsWith('U') ? JsonValue.Create(ulong.Parse(plain, CultureInfo.InvariantCulture)) : JsonValue.Create(long.Parse(plain, CultureInfo.InvariantCulture));
        }
        return JsonValue.Create(text);
    }

    public static JsonNode? Select(JsonNode? value, string? memberPath)
    {
        if (string.IsNullOrEmpty(memberPath)) return value?.DeepClone();
        if (!System.Text.RegularExpressions.Regex.IsMatch(memberPath, @"^[A-Za-z_][A-Za-z0-9_]*(?:\[\d+\])*(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[\d+\])*)*$"))
            throw new OperationException("watch_member", "Use member names, dots and numeric array indices only.");
        foreach (System.Text.RegularExpressions.Match part in System.Text.RegularExpressions.Regex.Matches(memberPath, @"([A-Za-z_][A-Za-z0-9_]*)|\[(\d+)\]"))
        {
            value = part.Groups[1].Success ? value is JsonObject obj ? obj[part.Value] : null :
                value is JsonArray array && int.TryParse(part.Groups[2].Value, out var i) && i < array.Count ? array[i] : null;
            if (value is null) throw new OperationException("watch_member", "The selected member or array index is absent.");
        }
        return value?.DeepClone() ?? throw new OperationException("watch_member", "Selected member is null.");
    }
}
