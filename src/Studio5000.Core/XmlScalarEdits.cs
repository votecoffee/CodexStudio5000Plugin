using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

internal static class XmlScalarEdits
{
    public const string Preservation = "scalar-byte-spans-v1";
    public const string Serialization = "document-serialization-v1";
    public static bool Supports(Edit[] edits) => edits.All(e => e.Operation is "set_text" or "set_attribute");
    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Attribute(string value, char quote) => Escape(value).Replace(quote.ToString(), quote == '"' ? "&quot;" : "&apos;").Replace("\r", "&#xD;").Replace("\n", "&#xA;").Replace("\t", "&#x9;");
    public static byte[] Apply(byte[] baseline, Edit[] edits, Func<byte[], XDocument> parse, Action<XDocument, Edit> apply,
        Action<XDocument> validate, bool cdata, Action<XDocument, Edit>? beforeEdit)
    {
        var doc = parse(baseline); var source = new XmlSourceDocument(baseline, doc);
        var texts = new Dictionary<XElement, string>(); var attributes = new Dictionary<XAttribute, string>();
        foreach (var edit in edits)
        {
            var target = XmlProjects.Select(doc, edit.XPath); beforeEdit?.Invoke(doc, edit);
            // The domain operation validates target restrictions before recording an edit.
            var original = target.Value; var attribute = edit.Operation == "set_attribute" && edit.Attribute is not null ? target.Attribute(edit.Attribute) : null;
            var originalAttribute = attribute?.Value; apply(doc, edit); XmlConvert.VerifyXmlChars(edit.Value ?? "");
            if (edit.Operation == "set_text") texts.TryAdd(target, original);
            else if (attribute is not null) attributes.TryAdd(attribute, originalAttribute!);
        }
        validate(doc); var patches = new List<XmlSourceDocument.Patch>();
        var expectedText = new Dictionary<XElement, string>();
        foreach (var (target, original) in texts)
        {
            var value = target.Value; expectedText[target] = original;
            if (value == original) continue;
            var span = source.Locate(target);
            var content = cdata ? "<![CDATA[" + value.Replace("]]>", "]]]]><![CDATA[>") + "]]>" : Escape(value).Replace("\r", "&#xD;");
            expectedText[target] = cdata ? value.Replace("\r\n", "\n").Replace('\r', '\n') : value;
            patches.Add(span.Empty ? new(span.OpenEnd - 2, span.OpenEnd, ">" + content + "</" + span.QualifiedName + ">") : new(span.OpenEnd, span.CloseStart, content));
        }
        foreach (var (attribute, original) in attributes)
        {
            if (attribute.Value == original) continue;
            var span = source.AttributeValue(attribute); patches.Add(new(span.Start, span.End, Attribute(attribute.Value, span.Quote)));
        }
        var bytes = source.Apply(patches); var actual = parse(bytes); validate(actual);
        // Verify selected final values independently after parsing the patched bytes.
        // Scalar edits preserve the element inventory, so positions remain stable even
        // when earlier value-dependent XPaths no longer select their original target.
        var oldNodes = doc.Descendants().ToArray(); var newNodes = actual.Descendants().ToArray();
        if (oldNodes.Length != newNodes.Length) throw new OperationException("xml_preservation_mismatch", "Scalar edits changed the XML element inventory.");
        for (var i = 0; i < oldNodes.Length; i++)
        {
            var node = oldNodes[i]; var result = newNodes[i];
            if (node.Name != result.Name || (expectedText.TryGetValue(node, out var expected) && result.Value != expected) ||
                node.Attributes().Any(a => attributes.ContainsKey(a) && (string?)result.Attribute(a.Name) != a.Value))
                throw new OperationException("xml_preservation_mismatch", "Patched XML does not retain the requested scalar values and element identities.");
        }
        return bytes;
    }
}
