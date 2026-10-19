using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record ComparedRoutine(string Owner, string Name, string Change, string? LeftType, string? RightType, string? LeftXPath, string? RightXPath, int LeftRungs, int RightRungs);
public sealed record ComparedRoutinesReport(ProjectInfo Left, ProjectInfo Right, int Total, int Offset, ComparedRoutine[] Routines);
public sealed record LadderCompareRequest(string LeftProjectId, string LeftSha256, string RightProjectId, string RightSha256,
    string? LeftRoutineXPath, string? RightRoutineXPath, int Offset = 0, int Limit = 1, bool IncludeRungNumbers = false, bool ChangesOnly = false);
public sealed record ComparedRung(int Position, string Change, LadderRungPreview? Left, LadderRungPreview? Right);
public sealed record LadderComparisonReport(ProjectInfo Left, ProjectInfo Right, int Total, int Offset, ComparedRung[] Rungs, string Coverage);

public static class LadderComparison
{
    internal static IEnumerable<(XElement? Left, XElement? Right)> Routines(XDocument left, XDocument right)
    {
        static string Key(XElement e) => Owner(e) + "/" + (string?)e.Attribute("Name");
        var a = left.Descendants("Routine").ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var b = right.Descendants("Routine").ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        return a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(k => (a.GetValueOrDefault(k), b.GetValueOrDefault(k)));
    }
    internal static string Owner(XElement e) => e.Ancestors("Program").FirstOrDefault() is { } p ? "Program:" + (string?)p.Attribute("Name") :
        e.Ancestors("AddOnInstructionDefinition").FirstOrDefault() is { } a ? "AOI:" + (string?)a.Attribute("Name") : "Controller";
    internal static string Canon(XElement e, bool numbers) => ProjectComparison.Normalize(e, numbers).ToString(SaveOptions.DisableFormatting);
    public static LadderComparisonReport Compare(ProjectInfo left, XElement? leftRoutine, ProjectInfo right, XElement? rightRoutine, LadderCompareRequest request)
    {
        if (request.Offset is < 0 or > 100000 || request.Limit is < 1 or > 20 || leftRoutine is null && rightRoutine is null)
            throw new OperationException("invalid_ladder_query", "Select at least one exact RLL routine; use offset 0..100000 and limit 1..20.");
        foreach (var routine in new[] { leftRoutine, rightRoutine }.OfType<XElement>())
            if (routine.Name != "Routine" || (string?)routine.Attribute("Type") != "RLL") throw new OperationException("not_ladder", "Both selected routines must be RLL when present.");
        static XElement[] Rungs(XElement? r)
        {
            var contents = r?.Elements("RLLContent").ToArray() ?? [];
            if (contents.Length > 1 || contents.SelectMany(c => c.Elements()).Any(e => e.Name != "Rung")) throw new OperationException("unsupported_comparison", "Unsupported ladder content.");
            return contents.SelectMany(c => c.Elements("Rung")).ToArray();
        }
        var a = Rungs(leftRoutine); var b = Rungs(rightRoutine);
        var aligned = ProjectComparison.Align(a.Select(e => Canon(e, request.IncludeRungNumbers)).ToArray(), b.Select(e => Canon(e, request.IncludeRungNumbers)).ToArray());
        var rows = new List<(int? A, int? B, string Change)>(); var removed = new List<int>(); var added = new List<int>();
        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
                rows.Add((i < removed.Count ? removed[i] : null, i < added.Count ? added[i] : null, i < removed.Count && i < added.Count ? "modified" : i < removed.Count ? "removed" : "added"));
            removed.Clear(); added.Clear();
        }
        foreach (var pair in aligned)
        {
            if (pair.Left >= 0 && pair.Right >= 0) { Flush(); rows.Add((pair.Left, pair.Right, "unchanged")); }
            else { if (pair.Left >= 0) removed.Add(pair.Left); if (pair.Right >= 0) added.Add(pair.Right); }
        }
        Flush();
        LadderRungPreview? Render(ProjectInfo info, XElement? routine, int? index) => index is null ? null :
            LadderPreview.Build(info, routine!, new(info.CurrentSha256, XmlProjects.PathOf(routine!), index.Value, 1)).Rungs.Single();
        var selected = rows.Select((r, i) => (Row: r, Position: i)).Where(r => !request.ChangesOnly || r.Row.Change != "unchanged").ToArray();
        var result = new LadderComparisonReport(left, right, selected.Length, request.Offset,
            selected.Skip(request.Offset).Take(request.Limit).Select(r => new ComparedRung(r.Position, r.Row.Change, Render(left, leftRoutine, r.Row.A), Render(right, rightRoutine, r.Row.B))).ToArray(),
            "Independent saved-project comparison and passive ladder geometry. Occurrence alignment preserves inserted/deleted gaps; adjacent unmatched spans pair by order. Unknown instructions retain source/diagnostics. No Studio compare, live power-flow, simulation or semantic equivalence.");
        if (JsonSerializer.SerializeToUtf8Bytes(result, ProjectService.Json).Length > 4 * 1024 * 1024) throw new OperationException("comparison_page_too_large", "Select fewer rungs.");
        return result;
    }
}

public sealed partial class ProjectService
{
    private (ProjectInfo Left, XDocument A, ProjectInfo Right, XDocument B) ComparisonDocuments(string leftId, string leftHash, string rightId, string rightHash)
    {
        var left = Get(leftId); var right = Get(rightId); Expect(left, leftHash); Expect(right, rightHash);
        if (left.Kind != "logix" || right.Kind != "logix") throw new OperationException("not_logix", "Select Logix snapshots.");
        var a = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(left.Id, left.Revision))); var b = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(right.Id, right.Revision)));
        ProjectComparison.ValidateSize(a); ProjectComparison.ValidateSize(b); return (left, a, right, b);
    }
    public ComparedRoutinesReport CompareRoutines(ProjectCompareRequest request)
    {
        if (request.Offset is < 0 or > 100000 || request.Limit is < 1 or > 200 || request.Query?.Length > 256) throw new OperationException("invalid_comparison", "Use offset 0..100000, limit 1..200 and query up to 256 characters.");
        var (left, a, right, b) = ComparisonDocuments(request.LeftProjectId, request.LeftSha256, request.RightProjectId, request.RightSha256);
        var rows = LadderComparison.Routines(a, b).Select(pair =>
        {
            var e = (pair.Right ?? pair.Left)!;
            return new ComparedRoutine(LadderComparison.Owner(e), (string)e.Attribute("Name")!, pair.Left is null ? "added" : pair.Right is null ? "removed" :
                LadderComparison.Canon(pair.Left, request.IncludeRungNumbers) == LadderComparison.Canon(pair.Right, request.IncludeRungNumbers) ? "unchanged" : "modified",
                (string?)pair.Left?.Attribute("Type"), (string?)pair.Right?.Attribute("Type"), pair.Left is null ? null : XmlProjects.PathOf(pair.Left), pair.Right is null ? null : XmlProjects.PathOf(pair.Right),
                pair.Left?.Elements("RLLContent").Elements("Rung").Count() ?? 0, pair.Right?.Elements("RLLContent").Elements("Rung").Count() ?? 0);
        }).Where(r => string.IsNullOrEmpty(request.Query) || (r.Owner + "/" + r.Name).Contains(request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(left, right, rows.Length, request.Offset, rows.Skip(request.Offset).Take(request.Limit).ToArray());
    }
    public LadderComparisonReport CompareLadder(LadderCompareRequest request)
    {
        var (left, a, right, b) = ComparisonDocuments(request.LeftProjectId, request.LeftSha256, request.RightProjectId, request.RightSha256);
        XElement? Select(XDocument doc, string? xpath)
        {
            if (xpath is null) return null;
            if (string.IsNullOrWhiteSpace(xpath) || xpath.Length > 8192) throw new OperationException("invalid_ladder_query", "Select an exact routine XPath up to 8192 characters.");
            return XmlProjects.Select(doc, xpath);
        }
        return LadderComparison.Compare(left, Select(a, request.LeftRoutineXPath), right, Select(b, request.RightRoutineXPath), request);
    }
}
