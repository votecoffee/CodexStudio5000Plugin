using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record ProjectCompareRequest(string LeftProjectId, string LeftSha256, string RightProjectId, string RightSha256,
    string? Query = null, int Offset = 0, int Limit = 100, bool IncludeRungNumbers = false);
public sealed record ProjectDifference(string Kind, string Path, string Change, string? LeftXPath, string? RightXPath,
    string? LeftSha256, string? RightSha256, string? LeftXml, string? RightXml, bool LeftTruncated, bool RightTruncated);
public sealed record ProjectComparisonReport(ProjectInfo Left, ProjectInfo Right, int TotalChanges, int FilteredChanges,
    int Added, int Removed, int Modified, int Offset, int Limit, ProjectDifference[] Differences, string Boundary);

public static class ProjectComparison
{
    private static readonly HashSet<string> Expanded = ["RSLogix5000Content", "Controller", "Programs", "Program", "Routines",
        "AddOnInstructionDefinitions", "AddOnInstructionDefinition", "Tags", "DataTypes", "Modules", "Tasks"];

    public static ProjectComparisonReport Compare(ProjectInfo left, XDocument leftDocument, ProjectInfo right,
        XDocument rightDocument, ProjectCompareRequest request) => CompareCore(left, leftDocument, right, rightDocument, request, false);

    internal static ProjectComparisonReport CompareForReview(ProjectInfo left, XDocument leftDocument, ProjectInfo right,
        XDocument rightDocument, ProjectCompareRequest request) => CompareCore(left, leftDocument, right, rightDocument, request with { Offset = 0, Limit = 200 }, true);

    private static ProjectComparisonReport CompareCore(ProjectInfo left, XDocument leftDocument, ProjectInfo right,
        XDocument rightDocument, ProjectCompareRequest request, bool allMatches)
    {
        if (request.Offset is < 0 or > 100000 || request.Limit is < 1 or > 200 || request.Query?.Length > 256)
            throw new OperationException("invalid_comparison", "Use offset 0..100000, limit 1..200 and a query up to 256 characters.");
        ValidateSize(leftDocument); ValidateSize(rightDocument);
        var changes = new List<ProjectDifference>();
        string Canon(XElement element, bool header = false) => Normalize(element, request.IncludeRungNumbers, header).ToString(SaveOptions.DisableFormatting);
        void Add(XElement? a, XElement? b, string path, bool header = false)
        {
            var before = a is null ? null : Canon(a, header);
            var after = b is null ? null : Canon(b, header);
            if (before == after) return;
            static string? Hash(string? text) => text is null ? null : ProjectService.Hash(Encoding.UTF8.GetBytes(text));
            static string? Snip(string? text) => text is { Length: > 4096 } ? text[..4096] : text;
            changes.Add(new((b ?? a)!.Name.LocalName, path, a is null ? "added" : b is null ? "removed" : "modified",
                a is null ? null : XmlProjects.PathOf(a), b is null ? null : XmlProjects.PathOf(b), Hash(before), Hash(after),
                Snip(before), Snip(after), before?.Length > 4096, after?.Length > 4096));
        }
        void Walk(XElement? a, XElement? b, string path)
        {
            if (a is null || b is null || a.Name != b.Name) { Add(a, b, path); return; }
            if (a.Name == "RLLContent")
            {
                Add(a, b, path + "/@metadata", true);
                var oldRungs = a.Elements().ToArray(); var newRungs = b.Elements().ToArray();
                if (oldRungs.Any(e => e.Name != "Rung") || newRungs.Any(e => e.Name != "Rung"))
                    throw new OperationException("unsupported_comparison", "RLLContent contains unsupported non-rung elements.");
                var matches = Align(oldRungs.Select(e => Canon(e)).ToArray(), newRungs.Select(e => Canon(e)).ToArray());
                var removed = new List<int>(); var added = new List<int>();
                void Flush()
                {
                    for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
                    {
                        var oldRung = i < removed.Count ? oldRungs[removed[i]] : null;
                        var newRung = i < added.Count ? newRungs[added[i]] : null;
                        var ordinal = i < added.Count ? added[i] : removed[i];
                        Add(oldRung, newRung, path + $"/Rung[position={ordinal}]");
                    }
                    removed.Clear(); added.Clear();
                }
                foreach (var pair in matches)
                {
                    if (pair.Left >= 0 && pair.Right >= 0) Flush();
                    else { if (pair.Left >= 0) removed.Add(pair.Left); if (pair.Right >= 0) added.Add(pair.Right); }
                }
                Flush(); return;
            }
            if (!Expanded.Contains(a.Name.LocalName) && !(a.Name == "Routine" && (string?)a.Attribute("Type") == "RLL" && (string?)b.Attribute("Type") == "RLL"))
            { Add(a, b, path); return; }
            Add(a, b, path + "/@metadata", true);
            var oldChildren = Children(a); var newChildren = Children(b);
            foreach (var key in oldChildren.Keys.Union(newChildren.Keys, StringComparer.OrdinalIgnoreCase))
            {
                oldChildren.TryGetValue(key, out var oldChild); newChildren.TryGetValue(key, out var newChild);
                Walk(oldChild, newChild, path + "/" + key);
            }
        }
        Walk(leftDocument.Root, rightDocument.Root, "/RSLogix5000Content");
        var selected = changes.Where(c => string.IsNullOrEmpty(request.Query) ||
            c.Path.Contains(request.Query, StringComparison.OrdinalIgnoreCase) || c.Kind.Contains(request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (allMatches && selected.Length > ComparisonReviews.MaximumChanges)
            throw new OperationException("comparison_review_limit", "A review supports up to 5000 matching changes; narrow the comparison filter.");
        var result = new ProjectComparisonReport(left, right, changes.Count, selected.Length,
            changes.Count(c => c.Change == "added"), changes.Count(c => c.Change == "removed"), changes.Count(c => c.Change == "modified"),
            request.Offset, allMatches ? selected.Length : request.Limit, allMatches ? selected : selected.Skip(request.Offset).Take(request.Limit).ToArray(),
            "Structural XML comparison only. Attribute order/formatting indentation and root ExportDate/Controller LastModifiedDate are ignored; scalar text, stored values, UDT member/AOI parameter order and unknown component XML remain significant. Rung numbers are optional. LCS aligns rung occurrences; moves are not inferred. No native semantic equivalence or runtime validation.");
        if (!allMatches && JsonSerializer.SerializeToUtf8Bytes(result, ProjectService.Json).Length > 4 * 1024 * 1024)
            throw new OperationException("comparison_page_too_large", "Comparison page exceeds 4 MiB; request a smaller limit.");
        return result;
    }

    private static Dictionary<string, XElement> Children(XElement parent)
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in parent.Elements())
        {
            var identity = child.Name == "Controller" ? null : (string?)child.Attribute("Name") ?? (string?)child.Attribute("OnlineEditType");
            var key = child.Name + (identity is not null ? "[" + identity + "]" : "[" + (child.ElementsBeforeSelf(child.Name).Count() + 1) + "]");
            if (!result.TryAdd(key, child)) throw new OperationException("ambiguous_comparison", "Duplicate child identities prevent structural comparison.");
        }
        return result;
    }

    internal static XElement Normalize(XElement element, bool rungNumbers, bool header = false)
    {
        var result = new XElement(element.Name, element.Attributes().Where(a =>
            !(element.Name == "RSLogix5000Content" && a.Name == "ExportDate") &&
            !(element.Name == "Controller" && a.Name == "LastModifiedDate") &&
            !(!rungNumbers && element.Name == "Rung" && a.Name == "Number")).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a)));
        foreach (var node in element.Nodes())
            switch (node)
            {
                case XElement child when !header: result.Add(Normalize(child, rungNumbers)); break;
                case XText text when !element.HasElements || !string.IsNullOrWhiteSpace(text.Value): result.Add(new XText(text.Value)); break;
                case XComment comment: result.Add(new XComment(comment.Value)); break;
                case XProcessingInstruction instruction: result.Add(new XProcessingInstruction(instruction.Target, instruction.Data)); break;
            }
        return result;
    }

    internal static void ValidateSize(XDocument document)
    {
        var count = 0;
        foreach (var element in document.Descendants())
            if (++count > 100000 || element.Ancestors().Take(65).Count() > 64)
                throw new OperationException("comparison_limit", "Comparison supports 100000 XML elements and 64 nesting levels per project.");
    }

    internal static List<(int Left, int Right)> Align(string[] left, string[] right)
    {
        var result = new List<(int, int)>();
        var prefix = 0;
        while (prefix < Math.Min(left.Length, right.Length) && left[prefix] == right[prefix]) { result.Add((prefix, prefix)); prefix++; }
        var suffix = 0;
        while (suffix < Math.Min(left.Length, right.Length) - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        var n = left.Length - prefix - suffix; var m = right.Length - prefix - suffix;
        if ((long)(n + 1) * (m + 1) > 1000000)
            throw new OperationException("comparison_limit", "Changed rung sequences exceed the 1000000-cell alignment budget; compare smaller project copies.");
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = left[prefix + i] == right[prefix + j] ? 1 + table[i + 1, j + 1] : Math.Max(table[i + 1, j], table[i, j + 1]);
        var a = 0; var b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && left[prefix + a] == right[prefix + b]) { result.Add((prefix + a++, prefix + b++)); }
            else if (a < n && (b == m || table[a + 1, b] >= table[a, b + 1])) result.Add((prefix + a++, -1));
            else result.Add((-1, prefix + b++));
        }
        for (var i = suffix; i > 0; i--) result.Add((left.Length - i, right.Length - i));
        return result;
    }
}
