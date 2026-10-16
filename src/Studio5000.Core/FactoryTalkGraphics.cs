using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;

namespace Studio5000.Core;

public static partial class FactoryTalkGraphics
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly string[] IdentityAttributes = ["name", "stateId", "id"];
    public static bool IsFactoryTalk(string kind) => kind is "ftview-me" or "ftview-se";
    [GeneratedRegex(@"^Gfx-(ME|SE)([1-9][0-9]?)\.xsd$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNamePattern();

    public static XDocument Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (doc.Root?.Name != "gfx") throw new OperationException("invalid_graphics", "Expected a FactoryTalk View gfx root element.");
        _ = SchemaName(doc);
        foreach (var parent in doc.Descendants())
            if (parent.Elements().Where(e => e.Attribute("name") != null)
                .GroupBy(e => (e.Name, Name: e.Attribute("name")!.Value), new ObjectKeyComparer()).Any(g => g.Count() > 1))
                throw new OperationException("duplicate_name", "Sibling object/connection names must be unique.");
        return doc;
    }

    private sealed class ObjectKeyComparer : IEqualityComparer<(XName, string Name)>
    {
        public bool Equals((XName, string Name) x, (XName, string Name) y) => x.Item1 == y.Item1 && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
        public int GetHashCode((XName, string Name) x) => HashCode.Combine(x.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(x.Name));
    }

    public static string SchemaName(XDocument doc)
    {
        var name = (string?)doc.Root?.Attribute(Xsi + "noNamespaceSchemaLocation") ?? "";
        if (!SchemaNamePattern().IsMatch(name))
            throw new OperationException("unsupported_schema", "Reference a local schema filename such as Gfx-ME17.xsd or Gfx-SE17.xsd; paths and URLs are not accepted.");
        return name;
    }

    public static string Kind(XDocument doc) => "ftview-" + SchemaNamePattern().Match(SchemaName(doc)).Groups[1].Value.ToLowerInvariant();

    public static GraphicsValidation Validate(XDocument doc, string schemaDirectory, bool requireSchema)
    {
        var name = SchemaName(doc);
        var edition = SchemaNamePattern().Match(name).Groups[1].Value;
        var path = Path.Combine(Path.GetFullPath(schemaDirectory), name);
        ProjectService.RejectLinks(path);
        if (!File.Exists(path))
        {
            if (requireSchema) throw new OperationException("schema_unavailable", $"Install/configure the matching FactoryTalk schema: {name}.");
            return new(edition, name, null, false, []);
        }
        var errors = new List<string>();
        var schemas = new XmlSchemaSet { XmlResolver = null };
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            schemas.Add(null, reader);
        schemas.Compile();
        doc.Validate(schemas, (_, e) => { if (errors.Count < 30) errors.Add(e.Message); });
        var report = new GraphicsValidation(edition, name, ProjectService.Hash(File.ReadAllBytes(path)), errors.Count == 0, errors.ToArray());
        if (errors.Count > 0) throw new OperationException("graphics_schema_invalid", string.Join("\n", errors));
        return report;
    }

    public static byte[] Apply(byte[] baseline, Edit[] edits, string schemaDirectory)
    {
        if (edits is null || edits.Length is 0 or > 200) throw new OperationException("invalid_edits", "Provide between 1 and 200 edits.");
        var doc = Parse(baseline);
        var schema = SchemaName(doc);
        foreach (var edit in edits)
        {
            var target = XmlProjects.Select(doc, edit.XPath);
            if (target == doc.Root) throw new OperationException("invalid_target", "The gfx root and schema identity are preserved.");
            switch (edit.Operation)
            {
                case "set_attribute":
                    if (edit.Attribute is null || IdentityAttributes.Contains(edit.Attribute) || target.Attribute(edit.Attribute) is not { } attribute || attribute.IsNamespaceDeclaration)
                        throw new OperationException("invalid_attribute", "Select an existing property attribute other than name/stateId/id.");
                    attribute.Value = edit.Value ?? "";
                    break;
                case "set_text":
                    if (target.HasElements) throw new OperationException("invalid_target", "Text edits require a leaf element.");
                    target.ReplaceNodes(new XText(edit.Value ?? ""));
                    break;
                case "replace_element":
                    using (var reader = XmlReader.Create(new StringReader(edit.Value ?? ""), new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                    {
                        var replacement = XElement.Load(reader, LoadOptions.PreserveWhitespace);
                        RequireSameShape(target, replacement);
                        target.ReplaceWith(replacement);
                    }
                    break;
                default: throw new OperationException("unsupported_graphics_edit", "FactoryTalk update imports support set_attribute, set_text and property-only replace_element. Object creation/deletion requires a separate native workflow.");
            }
        }
        if (SchemaName(doc) != schema) throw new OperationException("identity_change", "Schema identity must be preserved.");
        Validate(doc, schemaDirectory, requireSchema: true);
        return Serialize(doc);
    }

    private static void RequireSameShape(XElement before, XElement after)
    {
        if (before.Name != after.Name || IdentityAttributes.Any(a => (string?)before.Attribute(a) != (string?)after.Attribute(a)) ||
            !before.Attributes().Select(a => a.Name).ToHashSet().SetEquals(after.Attributes().Select(a => a.Name)))
            throw new OperationException("identity_change", "A graphics update must preserve element type, identity and property names.");
        var oldChildren = before.Elements().ToArray(); var newChildren = after.Elements().ToArray();
        if (oldChildren.Length != newChildren.Length)
            throw new OperationException("unsupported_graphics_edit", "Update imports preserve object/configuration structure; edit existing property values.");
        for (var i = 0; i < oldChildren.Length; i++) RequireSameShape(oldChildren[i], newChildren[i]);
    }

    public static GraphicsProperty[] Changes(XDocument before, XDocument after)
    {
        RequireSameShape(before.Root!, after.Root!);
        var changes = new List<GraphicsProperty>();
        void Compare(XElement a, XElement b)
        {
            foreach (var property in b.Attributes().Where(p => !p.IsNamespaceDeclaration))
                if ((string?)a.Attribute(property.Name) != property.Value)
                    changes.Add(new(StablePath(b), property.Name.ToString(), property.Value));
            if (!b.HasElements && a.Value != b.Value) changes.Add(new(StablePath(b), null, b.Value));
            foreach (var pair in a.Elements().Zip(b.Elements())) Compare(pair.First, pair.Second);
        }
        Compare(before.Root!, after.Root!);
        return changes.ToArray();
    }

    public static string StablePath(XElement element) => "/" + string.Join("/", element.AncestorsAndSelf().Reverse().Select(e =>
    {
        var key = IdentityAttributes.Select(a => e.Attribute(a)).FirstOrDefault(a => a != null);
        return e.Name.LocalName + (key != null ? $"[@{key.Name.LocalName}={XPathLiteral(key.Value)}]" : $"[{e.ElementsBeforeSelf(e.Name).Count() + 1}]");
    }));

    private static string XPathLiteral(string value) => !value.Contains('\'') ? "'" + value + "'" :
        !value.Contains('"') ? "\"" + value + "\"" : "concat('" + value.Replace("'", "',\"'\",'") + "')";

    public static byte[] Serialize(XDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.Entitize })) doc.Save(writer);
        return stream.ToArray();
    }
}
