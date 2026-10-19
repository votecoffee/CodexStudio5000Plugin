using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record GraphicsReferenceRequest(string ExpectedSha256, string? Query = null, int Offset = 0, int Limit = 200);
public sealed record GraphicsTagReference(string XPath, string ObjectXPath, string? ObjectName, string Attribute,
    string Expression, int Start, int Length, string Reference, string? Shortcut, string? TagPath, string Kind, string Resolution);
public sealed record GraphicsReferenceReport(ProjectInfo Project, int Total, int Offset, int Limit,
    GraphicsTagReference[] References, string[] Diagnostics, bool DiagnosticsTruncated, string Coverage);

public static partial class GraphicsReferences
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\\.:/\[\]]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareName();
    [GeneratedRegex(@"^#[0-9]+(?:\.[A-Za-z_][A-Za-z0-9_.\[\]]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();

    public static GraphicsReferenceReport Extract(ProjectInfo project, XDocument document, GraphicsReferenceRequest request)
    {
        if (request.Offset < 0 || request.Offset > 100000 || request.Limit is < 1 or > 200 || request.Query?.Length > 256)
            throw new OperationException("invalid_reference_query", "Use offset 0..100000, limit 1..200 and a query up to 256 characters.");
        var scan = Scan(document);
        var filtered = scan.References.Where(r => string.IsNullOrEmpty(request.Query) ||
            r.Reference.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ||
            r.ObjectName?.Contains(request.Query, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var report = new GraphicsReferenceReport(project, filtered.Length, request.Offset, request.Limit,
            filtered.Skip(request.Offset).Take(request.Limit).ToArray(), scan.Diagnostics, scan.Truncated,
            "Conservative expression/parameter-value scan: braced references and simple standalone names. No shortcut/parameter resolution, full expression compiler, VBA/command analysis, PLC existence check or native/runtime validation.");
        if (JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json).Length > 4 * 1024 * 1024)
            throw new OperationException("reference_page_too_large", "Reference page exceeds 4 MiB. Request a smaller limit.");
        return report;
    }

    internal static (GraphicsTagReference[] References, string[] Diagnostics, bool Truncated) Scan(XDocument document)
    {
        var references = new List<GraphicsTagReference>();
        var diagnostics = new List<string>();
        var diagnosticCount = 0;
        void Diagnose(string xpath, string code)
        {
            diagnosticCount++;
            if (diagnostics.Count < 50) diagnostics.Add(code + ": " + xpath);
        }
        foreach (var attribute in document.Descendants().Attributes().Where(a =>
            a.Name.LocalName.Equals("expression", StringComparison.OrdinalIgnoreCase) ||
            a.Parent?.Name.LocalName == "parameter" && a.Name.LocalName == "value"))
        {
            var expression = attribute.Value;
            if (expression.Length > 16 * 1024)
                throw new OperationException("reference_limit", "An expression exceeds the 16 KiB extraction limit.");
            var element = attribute.Parent!;
            var xpath = FactoryTalkGraphics.StablePath(element) + "/@" + attribute.Name.LocalName;
            if (xpath.Length > 8192) throw new OperationException("reference_limit", "An expression selector exceeds the extraction limit.");
            var owner = element.AncestorsAndSelf().FirstOrDefault(e =>
                e.Attribute("name") is not null && e.Name.LocalName is not ("connection" or "parameter")) ?? element;
            var ownerPath = FactoryTalkGraphics.StablePath(owner);
            void Add(int start, int length, string value)
            {
                value = value.Trim();
                if (value.Length == 0) { Diagnose(xpath, "empty_reference"); return; }
                string? shortcut = null;
                string? tag = value;
                var kind = "hmi";
                var resolution = "requires_hmi_database";
                if (value.Contains('#')) { kind = "parameter"; tag = null; resolution = "requires_parameter_context"; }
                else
                {
                    var qualified = value.StartsWith("::", StringComparison.Ordinal) ? value[2..] : value;
                    if (qualified.StartsWith('['))
                    {
                        var end = qualified.IndexOf(']');
                        if (end > 1 && end < qualified.Length - 1)
                        {
                            shortcut = qualified[1..end]; tag = qualified[(end + 1)..];
                            kind = "direct"; resolution = "requires_shortcut_mapping";
                        }
                        else { kind = "unresolved"; tag = null; resolution = "invalid_shortcut_syntax"; }
                    }
                    else if (qualified.Contains("::", StringComparison.Ordinal) || qualified.Contains('[') || qualified.Contains(']'))
                    { kind = "unresolved"; tag = null; resolution = "requires_qualified_context"; }
                }
                references.Add(new(xpath, ownerPath, (string?)owner.Attribute("name"), attribute.Name.LocalName,
                    expression, start, length, value, shortcut, tag, kind, resolution));
                if (references.Count > 10000) throw new OperationException("reference_limit", "A display exceeds 10000 extracted reference occurrences.");
            }
            var trimmed = expression.Trim();
            if (ParameterName().IsMatch(trimmed) || BareName().IsMatch(trimmed) && trimmed.ToLowerInvariant() is not ("true" or "false" or "and" or "or" or "not"))
            {
                Add(expression.IndexOf(trimmed, StringComparison.Ordinal), trimmed.Length, trimmed);
                continue;
            }
            var quoted = false;
            for (var index = 0; index < expression.Length; index++)
            {
                if (expression[index] == '"')
                {
                    if (quoted && index + 1 < expression.Length && expression[index + 1] == '"') { index++; continue; }
                    quoted = !quoted; continue;
                }
                if (quoted || expression[index] != '{') continue;
                var end = expression.IndexOf('}', index + 1);
                var nested = expression.IndexOf('{', index + 1);
                if (end < 0 || nested >= 0 && nested < end) { Diagnose(xpath, "malformed_braced_reference"); break; }
                Add(index, end - index + 1, expression[(index + 1)..end]);
                index = end;
            }
            if (quoted) Diagnose(xpath, "unterminated_string_literal");
            if (!trimmed.Contains('{') && !trimmed.StartsWith('"') && trimmed.Any(char.IsLetter))
                Diagnose(xpath, "complex_unbraced_expression_not_resolved");
        }
        return (references.ToArray(), diagnostics.ToArray(), diagnosticCount > diagnostics.Count);
    }
}
