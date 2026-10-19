using System.Globalization;
using System.Xml.Linq;
using L5xploderLib;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Studio5000.Core;

// The preserved vendor library implements the original layout, not upstream schema 2.
internal static class ExplodedCompatibility
{
    public const int SchemaVersion = 1;
    private const int OptionsLimit = 64 * 1024;

    public static void Stamp(string directory)
    {
        var path = Paths.GetOptionsFilePath(directory);
        File.WriteAllText(path, $"schema_version: {SchemaVersion}\n" + File.ReadAllText(path));
    }

    public static L5xSerializationOptions ReadOptions(string directory)
    {
        var path = Paths.GetOptionsFilePath(directory);
        if (!File.Exists(path)) return L5xSerializationOptions.DefaultOptions;
        if (new FileInfo(path).Length > OptionsLimit)
            throw InvalidOptions("Exploded export options exceed 64 KiB.");
        try
        {
            var text = File.ReadAllText(path);
            var parser = new Parser(new StringReader(text));
            var depth = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is AnchorAlias)
                    throw InvalidOptions("Exploded export options must not contain YAML aliases.");
                if (parser.Current is MappingStart or SequenceStart && ++depth > 1)
                    throw InvalidOptions("Exploded export options must be a flat YAML mapping.");
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
            }
            var yaml = new YamlStream();
            using var reader = new StringReader(text);
            yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
                throw InvalidOptions("Exploded export options must be one flat YAML mapping.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in mapping.Children)
            {
                if (entry.Key is not YamlScalarNode { Value: not null } key ||
                    entry.Value is not YamlScalarNode { Value: not null } value ||
                    !values.TryAdd(key.Value, value.Value))
                    throw InvalidOptions("Exploded export options require unique scalar keys and values.");
            }
            if (values.TryGetValue("schema_version", out var version) &&
                (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number != SchemaVersion))
                throw new OperationException("unsupported_exploded_schema",
                    $"This reader supports exploded schema {SchemaVersion} and legacy unstamped directories only. Use a matching reader for other layouts; do not remove or lower their version stamp.");
            foreach (var key in values.Keys)
                if (key is not ("schema_version" or "serialization_format" or "xml_attribute_per_line" or "omit_export_date" or "unsafe_skip_dependency_check"))
                    throw InvalidOptions("Exploded export options contain an unsupported setting.");
            if (values.TryGetValue("serialization_format", out var format) && !string.Equals(format, "Xml", StringComparison.OrdinalIgnoreCase))
                throw InvalidOptions("Only XML exploded serialization is supported.");
            if (ReadBool(values, "unsafe_skip_dependency_check", false))
                throw InvalidOptions("Unsafe dependency bypasses are not supported by this reader.");
            return new L5xSerializationOptions
            {
                PrettyXmlAttributes = ReadBool(values, "xml_attribute_per_line", false),
                OmitExportDate = ReadBool(values, "omit_export_date", true)
            };
        }
        catch (YamlException)
        {
            throw InvalidOptions("Exploded export options contain invalid YAML.");
        }
    }

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        return bool.TryParse(value, out var result) ? result : throw InvalidOptions("Exploded boolean settings must be true or false.");
    }

    private static OperationException InvalidOptions(string message) => new("invalid_exploded_options", message);

    public static void ValidateXml(XDocument document)
    {
        var root = document.Root;
        if (root is null) return;
        var aoiRoots = root.Name.LocalName is "EncodedData" or "AddOnInstructionDefinition"
            ? new[] { root }
            : root.DescendantsAndSelf("AddOnInstructionDefinitions").SelectMany(e => e.Elements());
        if (aoiRoots.Any(e => e.DescendantsAndSelf().Any(n => n.Name.LocalName == "EncodedData")))
            throw new OperationException("unsupported_exploded_aoi",
                "The legacy exploded reader cannot order protected/encoded AOI dependencies, even when dependency metadata is present. Keep this project as L5X/ACD or use a dependency-aware tool.");

        // Schema 1 serializes ST code only; descriptions and extra XML metadata would disappear.
        foreach (var routine in root.DescendantsAndSelf("Routine").Where(e => (string?)e.Attribute("Type") == "ST"))
        {
            var metadata = routine.Attributes().Any(a => a.Name != "Name" && a.Name != "Type") ||
                routine.Elements().Any(e => e.Name != "STContent") || routine.Nodes().OfType<XComment>().Any() ||
                routine.Elements("STContent").Any(content =>
                    content.Attributes().Any(a => a.Name != "OnlineEditType") ||
                    content.Elements().Any(e => e.Name != "Line") || content.Nodes().OfType<XComment>().Any() ||
                    content.Elements("Line").Any(line => line.Attributes().Any(a => a.Name != "Number") ||
                        line.Elements().Any() || line.Nodes().OfType<XComment>().Any()));
            if (metadata)
                throw new OperationException("unsupported_exploded_metadata",
                    "Exploded schema 1 cannot preserve this structured-text routine's XML metadata. Use an L5X/ACD export or a compatible newer exploded tool.");
        }
    }
}
