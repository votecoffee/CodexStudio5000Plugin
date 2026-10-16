using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Studio5000.Core;

public static class XmlProjects
{
    private static readonly Dictionary<string, string> Containers = new()
    {
        ["Tags"] = "Tag", ["Routines"] = "Routine", ["Programs"] = "Program",
        ["DataTypes"] = "DataType", ["AddOnInstructionDefinitions"] = "AddOnInstructionDefinition",
        ["RLLContent"] = "Rung", ["STContent"] = "Line"
    };
    private static readonly HashSet<string> DeclarationContainers =
        ["Tags", "LocalTags", "Routines", "Programs", "DataTypes", "AddOnInstructionDefinitions", "Modules", "Tasks", "Members", "Parameters"];
    public static XDocument Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        Validate(doc);
        return doc;
    }

    public static void Validate(XDocument doc)
    {
        if (doc.Root?.Name != "RSLogix5000Content" || doc.Root.Element("Controller") is null)
            throw new OperationException("invalid_project", "Expected an RSLogix5000Content/Controller L5X document.");
        foreach (var parent in doc.Descendants().Where(e => DeclarationContainers.Contains(e.Name.LocalName)))
        {
            var duplicate = parent.Elements().Where(e => e.Attribute("Name") != null)
                .GroupBy(e => (e.Name, Name: ((string)e.Attribute("Name")!).ToUpperInvariant()))
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new OperationException("duplicate_name", $"Duplicate {duplicate.Key.Name} in {parent.Name}.");
        }
        foreach (var routine in doc.Descendants("Routine"))
        {
            var type = (string?)routine.Attribute("Type");
            if (type is "RLL" && routine.Element("RLLContent") is null ||
                type is "ST" && routine.Element("STContent") is null)
                throw new OperationException("invalid_routine", "Routine content does not match its declared RLL/ST type.");
        }
    }

    public static XElement Select(XDocument doc, string xpath)
    {
        var nodes = doc.XPathSelectElements(xpath).Take(2).ToArray();
        if (nodes.Length != 1)
            throw new OperationException("ambiguous_target", "XPath must select exactly one existing element.");
        return nodes[0];
    }

    public static string PathOf(XElement e) => "/" + string.Join("/", e.AncestorsAndSelf().Reverse()
        .Select(n => $"{n.Name.LocalName}[{n.ElementsBeforeSelf(n.Name).Count() + 1}]"));

    public static byte[] Apply(byte[] baseline, Edit[] edits)
    {
        if (edits is null || edits.Length is 0 or > 200)
            throw new OperationException("invalid_edits", "Provide between 1 and 200 edits.");
        var doc = Parse(baseline);
        foreach (var edit in edits)
        {
            var target = Select(doc, edit.XPath);
            // Identity changes require a reference-aware operation; never silently rename a declaration.
            switch (edit.Operation)
            {
                case "set_attribute":
                    if (edit.Attribute is null or "Name" or "Number" || target.Attribute(edit.Attribute) is null)
                        throw new OperationException("invalid_attribute", "Select an existing attribute other than Name/Number.");
                    if (!target.AncestorsAndSelf().Any(e => e.Name == "Tag" || e.Name == "Routine"))
                        throw new OperationException("invalid_target", "Attribute edits must be inside a Tag or Routine.");
                    target.SetAttributeValue(edit.Attribute, edit.Value ?? "");
                    break;
                case "set_text":
                    if (target.HasElements || target.Name.LocalName is not ("Description" or "Text" or "Comment"))
                        throw new OperationException("invalid_target", "Text edits require a scalar Description, Text, or Comment element.");
                    target.ReplaceNodes(new XCData(edit.Value ?? ""));
                    break;
                case "replace_element":
                    RequireComponent(target);
                    var replacement = Fragment(edit.Value);
                    if (replacement.Name != target.Name || (string?)replacement.Attribute("Name") != (string?)target.Attribute("Name"))
                        throw new OperationException("identity_change", "Replacement must preserve element type and Name.");
                    target.ReplaceWith(replacement);
                    break;
                case "add_element":
                    var child = Fragment(edit.Value);
                    if (!Containers.TryGetValue(target.Name.LocalName, out var childName) || child.Name != childName)
                        throw new OperationException("invalid_container", "Unsupported component/container pair.");
                    target.Add(child);
                    break;
                case "remove_element":
                    RequireComponent(target);
                    target.Remove();
                    break;
                default: throw new OperationException("unknown_edit", $"Unknown edit operation: {edit.Operation}");
            }
        }
        Validate(doc);
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None,
            OmitXmlDeclaration = doc.Declaration is null
        })) doc.Save(writer);
        return stream.ToArray();
    }

    private static XElement Fragment(string? xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml ?? ""), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        return XElement.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void RequireComponent(XElement target)
    {
        if (target.Parent is null || !Containers.TryGetValue(target.Parent.Name.LocalName, out var kind) || target.Name != kind)
            throw new OperationException("invalid_target", "Select a tag, routine, rung, ST line, program, UDT, or AOI component.");
    }
}
