using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record MergeSnapshot(string ProjectId, string Sha256);
public sealed record LogicValueResolution(string TagName, string? ProgramName, string Choice);
public sealed record LogicValueMergeRequest(MergeSnapshot Baseline, MergeSnapshot Logic, MergeSnapshot Values, TagValueMap[]? Tags = null, LogicValueResolution[]? Resolutions = null);
public sealed record LogicValueMergeRow(string TagName, string? ProgramName, string Status, string? Baseline, string? Logic, string? Fresh, string? Detail);
public sealed record LogicValueMergeReport(ComparisonSnapshot Baseline, ComparisonSnapshot Logic, ComparisonSnapshot Values, string ReviewSha256,
    string ResultSha256, bool CanApply, int CopiedValues, LogicValueMergeRow[] Rows, string[] Conflicts, string Boundary);
public sealed record LogicValueMergeApplyRequest(LogicValueMergeRequest Preview, string ExpectedReviewSha256);

public sealed partial class ProjectService
{
    private (LogicValueMergeReport Report, byte[] Bytes) BuildLogicValueMerge(LogicValueMergeRequest request)
    {
        (ProjectInfo Info, XDocument Doc) Load(MergeSnapshot snapshot)
        { var p = Get(snapshot.ProjectId); Expect(p, snapshot.Sha256); if (p.Kind != "logix") throw new OperationException("not_logix", "Select Logix snapshots."); var doc = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(p.Id, p.Revision))); ProjectComparison.ValidateSize(doc); return (p, doc); }
        var (baseline, a) = Load(request.Baseline); var (logic, b) = Load(request.Logic); var (fresh, c) = Load(request.Values);
        static string Definition(XDocument doc)
        {
            var copy = new XDocument(doc);
            foreach (var tag in copy.Descendants("Tag").Where(t => t.Parent?.Name == "Tags" && t.Parent.Parent?.Name is { LocalName: "Program" or "Controller" })) tag.Elements("Data").Remove();
            return Hash(System.Text.Encoding.UTF8.GetBytes(ProjectComparison.Normalize(copy.Root!, true).ToString(SaveOptions.DisableFormatting)));
        }
        static string? Data(XElement? tag) => tag is null ? null : Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("", tag.Elements("Data").Select(d => ProjectComparison.Normalize(d, true).ToString(SaveOptions.DisableFormatting)))));
        var contextA = new LogicContext(a); var contextB = new LogicContext(b); var contextC = new LogicContext(c);
        var conflicts = new List<string>(); var rows = new List<LogicValueMergeRow>(); var copied = 0;
        if (Definition(c) != Definition(a) && Definition(c) != Definition(b)) conflicts.Add("fresh_project_definition_changed: fresh-values logic/configuration differs from both the baseline and modified-logic project.");
        var maps = request.Tags ?? b.Descendants("Tag").Where(t => t.Parent?.Name == "Tags" && t.Parent.Parent?.Name is { LocalName: "Program" or "Controller" } && (string?)t.Attribute("TagType") == "Base")
            .Select(t => new TagValueMap((string)t.Attribute("Name")!, (string?)t.Ancestors("Program").FirstOrDefault()?.Attribute("Name"), (string)t.Attribute("Name")!, (string?)t.Ancestors("Program").FirstOrDefault()?.Attribute("Name"))).ToArray();
        if (maps.Length is < 1 or > 256 || maps.Any(m => m is null) || maps.Select(m => (m.DestinationProgram + "." + m.DestinationTag).ToUpperInvariant()).Distinct().Count() != maps.Length)
            throw new OperationException("logic_value_merge_selection", "Select 1..256 unique whole-tag destinations.");
        var choices = request.Resolutions ?? [];
        if (choices.Any(r => r is null)) throw new OperationException("logic_value_resolution", "Resolutions cannot contain null entries.");
        foreach (var choice in choices) { WatchValidation.Identifier(choice.TagName); if (choice.ProgramName is not null) WatchValidation.Identifier(choice.ProgramName); if (choice.Choice is not ("logic" or "fresh")) throw new OperationException("logic_value_resolution", "Resolve value conflicts with logic or fresh."); }
        if (choices.Select(r => (r.ProgramName + "." + r.TagName).ToUpperInvariant()).Distinct().Count() != choices.Length) throw new OperationException("logic_value_resolution", "Resolve each destination only once.");
        foreach (var map in maps)
        {
            WatchValidation.Identifier(map.SourceTag); WatchValidation.Identifier(map.DestinationTag); if (map.SourceProgram is not null) WatchValidation.Identifier(map.SourceProgram); if (map.DestinationProgram is not null) WatchValidation.Identifier(map.DestinationProgram);
            var old = contextA.Tag(map.DestinationTag, map.DestinationProgram); var modified = contextB.Tag(map.DestinationTag, map.DestinationProgram); var current = contextC.Tag(map.SourceTag, map.SourceProgram);
            var oldHash = Data(old); var logicHash = Data(modified); var freshHash = Data(current);
            var choice = choices.SingleOrDefault(r => LogicContext.Same(r.TagName, map.DestinationTag) && LogicContext.Same(r.ProgramName, map.DestinationProgram))?.Choice;
            string status; string? detail = null;
            if (modified is null) { status = "conflict"; detail = "Selected destination is absent from the modified logic."; }
            else if (choice == "logic") status = "resolved-logic";
            else if (old is null) { status = current is null ? "preserved-new-logic-tag" : "conflict"; detail = "No common tag declaration exists in the baseline."; }
            else if (current is null) { status = "conflict"; detail = "Fresh-values tag is missing."; }
            else if (logicHash == freshHash || oldHash == freshHash) status = "preserved-logic";
            else if (logicHash != oldHash && choice != "fresh") { status = "conflict"; detail = "Both modified logic and fresh snapshot changed this stored value differently from the baseline."; }
            else
            {
                var item = TagValueMerging.Inspect(c, b, map);
                if (item.Row.Status is not ("ready" or "unchanged")) { status = "conflict"; detail = item.Row.Message; }
                else
                {
                    status = choice == "fresh" ? "resolved-fresh" : "copied-fresh";
                    if (item.Row.Status == "ready") { modified.Elements("Data").Remove(); modified.Add(item.Data!); copied++; }
                }
            }
            rows.Add(new(map.DestinationTag, map.DestinationProgram, status, oldHash, logicHash, freshHash, detail));
        }
        if (request.Tags is null)
        foreach (var old in a.Descendants("Tag").Where(t => t.Parent?.Name == "Tags" && t.Parent.Parent?.Name is { LocalName: "Program" or "Controller" }))
        {
            var name = (string)old.Attribute("Name")!; var scope = (string?)old.Ancestors("Program").FirstOrDefault()?.Attribute("Name"); var value = contextC.Tag(name, scope);
            if (contextB.Tag(name, scope) is null && value is not null && Data(old) != Data(value))
            {
                var keepRemoval = choices.Any(r => LogicContext.Same(r.TagName, name) && LogicContext.Same(r.ProgramName, scope) && r.Choice == "logic");
                rows.Add(new(name, scope, keepRemoval ? "resolved-logic-removal" : "conflict", Data(old), null, Data(value), "Modified logic removed a tag whose fresh stored value changed."));
                if (rows.Count > 512) throw new OperationException("merge_limit", "Too many removed-tag conflicts; select explicit whole-tag mappings.");
            }
        }
        foreach (var resolution in choices)
            if (!rows.Any(r => LogicContext.Same(r.TagName, resolution.TagName) && LogicContext.Same(r.ProgramName, resolution.ProgramName))) throw new OperationException("logic_value_resolution", "A resolution names a destination absent from this preview.");
        var bytes = TagValueMerging.Serialize(b); if (bytes.Length > 64 * 1024 * 1024) throw new OperationException("merge_limit", "Merged project exceeds 64 MiB.");
        var resultHash = Hash(bytes); var reviewHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, rows, conflicts, resultHash }, Json));
        static ComparisonSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.Name, p.Revision, p.CurrentSha256);
        var report = new LogicValueMergeReport(Snapshot(baseline), Snapshot(logic), Snapshot(fresh), reviewHash, resultHash, conflicts.Count == 0 && rows.All(r => r.Status != "conflict"), copied, rows.ToArray(), conflicts.ToArray(),
            "Three-source offline merge into a new managed project: modified logic/configuration is preserved and selected validated stored values come from the fresh snapshot. Baseline hashes expose independent value edits; unknown layouts and divergent fresh logic block apply. Explicit resolutions never bypass schema checks. Original projects are preserved. Fresh saved/uploaded values do not establish simultaneous live state or runtime acceptance.");
        return (report, bytes);
    }
    public async Task<LogicValueMergeReport> PreviewLogicValueMergeAsync(LogicValueMergeRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return BuildLogicValueMerge(request).Report; } finally { gate.Release(); } }
    public async Task<ProjectInfo> ApplyLogicValueMergeAsync(LogicValueMergeApplyRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var (report, bytes) = BuildLogicValueMerge(request.Preview);
            if (!report.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Merge differs from its reviewed baseline/logic/value inputs or resolutions.");
            if (!report.CanApply) throw new OperationException("merge_blocked", "Resolve every reported conflict before applying.");
            return PublishDiagnosticProject(bytes, "Logic/value merge", "logic-value-merge:" + report.Logic.Id);
        }
        finally { gate.Release(); }
    }
    private ProjectInfo PublishDiagnosticProject(byte[] bytes, string name, string origin)
    {
        var id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(ProjectDir(id)); AtomicBytes(Path.Combine(ProjectDir(id), "original.l5x"), bytes); AtomicBytes(RevisionFile(id, 0), bytes);
        var info = new ProjectInfo(id, name, origin, Hash(bytes), Hash(bytes), 0, DateTimeOffset.UtcNow); Save(ProjectMeta(id), info); Audit("diagnostic_project_copy", info, null, info.CurrentSha256, "Created preserved offline working copy."); return info;
    }
}
