using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record MessageFieldChoice(string TagName, string Attribute, string Choice, string? ProgramName = null);
public sealed record MergeFieldFinding(string Kind, string Location, string Status, string? BaselineSha256, string? LogicSha256,
    string? FreshSha256, string Detail, string? BaselineValue = null, string? LogicValue = null, string? FreshValue = null);
public sealed record MergePreservationReceipt(int TagRecords, int ReviewedRoutines, int ReplacedRoutines, int AddedRoutines,
    int RemovedRoutines, int MessageFieldsApplied, Dictionary<string, int> PreservedRepresentations,
    Dictionary<string, int> FreshRepresentationsDifferentFromLogic, bool UneditedSourceBytesPreserved, string Scope);

internal static class LogixPreservation
{
    public const string Boundary = "Offline, hash-bound UTF-8 preservation only. Keep every fresh tag record and Data representation, alias, declaration and configuration byte outside reviewed program-routine edits and explicit MESSAGE attribute choices. No value conversion or mixing of L5K/decorated data. AOI/layout edits and safety routine edits are unsupported. Native import/compile/readback and runtime/physical acceptance are separate; no controller connection.";
    public static string? Fingerprint(XElement? node) => node is null ? null : ProjectService.Hash(Encoding.UTF8.GetBytes(ProjectComparison.Normalize(node, true).ToString(SaveOptions.DisableFormatting)));
    public static string? TextHash(string? value) => value is null ? null : ProjectService.Hash(Encoding.UTF8.GetBytes(value));
    public static string PolicyHash(LogicValueMergeRequest request) => ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, policy = request }, ProjectService.Json));
    public static string Key(string? program, string name) => (program is null ? "Controller" : "Program/" + program) + "/" + name;
    public static Dictionary<string, XElement> Tags(XDocument document) => Index(document.Descendants("Tag").Where(t => t.Parent?.Name == "Tags" && t.Parent.Parent?.Name.LocalName is "Controller" or "Program"),
        t => Key((string?)t.Ancestors("Program").FirstOrDefault()?.Attribute("Name"), (string)t.Attribute("Name")!));
    public static Dictionary<string, XElement> Routines(XDocument document) => Index(document.Root!.Element("Controller")!.Elements("Programs").Elements("Program").Elements("Routines").Elements("Routine"),
        r => Key((string?)r.Parent!.Parent!.Attribute("Name"), (string)r.Attribute("Name")!));
    public static Dictionary<string, XElement> Index(IEnumerable<XElement> nodes, Func<XElement, string> key)
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes) if (!result.TryAdd(key(node), node)) throw new OperationException("merge_identity", "Duplicate source identities are unsupported.");
        return result;
    }
    public static XElement Declaration(XElement tag) => new(tag.Name, tag.Attributes().Select(a => new XAttribute(a)), tag.Nodes().Where(n =>
        !(n is XElement e && e.Name == "Data") && !(n is XText and not XCData && tag.HasElements && string.IsNullOrWhiteSpace(((XText)n).Value))));
    public static string? TagContract(XElement? tag) => tag is null ? null : Fingerprint(new XElement("Tag", tag.Attributes().Where(a => a.Name.LocalName is "Name" or "TagType" or "DataType" or "Dimensions" or "AliasFor" or "Class").Select(a => new XAttribute(a))));
    public static XElement? Message(XElement? tag) => tag?.Elements("Data").SingleOrDefault(d => (string?)d.Attribute("Format") == "Message")?.Element("MessageParameters");
    public static bool Safety(XElement node) => node.AncestorsAndSelf().Any(e => string.Equals((string?)e.Attribute("Class"), "Safety", StringComparison.OrdinalIgnoreCase));
    public static XElement LayoutContract(XElement controller, string section)
    {
        if (section == "DataTypes")
        { var types = new XElement(controller.Element(section) ?? new XElement(section)); types.Descendants("Description").Remove(); return types; }
        static XElement Entry(XElement node) => new(node.Name, node.Attributes().Where(a => a.Name.LocalName is
            "Name" or "DataType" or "Dimensions" or "Usage" or "Class" or "TagType" or "AliasFor" or "Required" or "Visible" or "Constant").Select(a => new XAttribute(a)));
        return new XElement(section, controller.Elements(section).Elements("AddOnInstructionDefinition").Select(aoi =>
            new XElement("AddOnInstructionDefinition", aoi.Attributes().Where(a => a.Name.LocalName is "Name" or "Class").Select(a => new XAttribute(a)),
                new XElement("Parameters", aoi.Elements("Parameters").Elements().Select(Entry)), new XElement("LocalTags", aoi.Elements("LocalTags").Elements().Select(Entry)))));
    }
    public static void ValidateChoices(MessageFieldChoice[] choices)
    {
        if (choices.Length > 256 || choices.Any(c => c is null)) throw new OperationException("merge_policy", "Use at most 256 explicit MESSAGE field choices.");
        foreach (var choice in choices)
        {
            WatchValidation.Identifier(choice.TagName); if (choice.ProgramName is not null) WatchValidation.Identifier(choice.ProgramName);
            if (string.IsNullOrWhiteSpace(choice.Attribute)) throw new OperationException("merge_policy", "Choose an explicit MESSAGE attribute.");
            try { XmlConvert.VerifyNCName(choice.Attribute); } catch (XmlException) { throw new OperationException("merge_policy", "A MESSAGE attribute must be an unqualified XML name."); }
            if (choice.Attribute is "xmlns" || choice.Choice is not ("logic" or "fresh")) throw new OperationException("merge_policy", "Choose logic or fresh for an exact MESSAGE attribute.");
        }
        if (choices.Select(c => Key(c.ProgramName, c.TagName).ToUpperInvariant() + "/" + c.Attribute).Distinct(StringComparer.Ordinal).Count() != choices.Length)
            throw new OperationException("merge_policy", "Choose each MESSAGE field only once.");
    }
    // Only attributes on an already parsed MessageParameters opening delimiter.
    // Existing spelling/spacing/quotes survive for every unselected attribute.
    public static LogixLexicalDocument.Patch MessagePatch(LogixLexicalDocument source, XElement node, Dictionary<string, string?> values)
    {
        var span = source.Locate(node); var opening = source.Text[span.Start..span.OpenEnd];
        var attributes = Regex.Matches(opening, "(?<space>\\s+)(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)\\s*=\\s*(?<quote>['\"])(?<value>.*?)\\k<quote>", RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Cast<Match>().ToDictionary(m => m.Groups["name"].Value, StringComparer.Ordinal);
        var edits = new List<(int Start, int End, string Text)>();
        foreach (var (name, value) in values)
        {
            var encoded = value is null ? null : SecurityElement.Escape(value)!;
            if (attributes.TryGetValue(name, out var attribute))
            {
                var field = attribute.Groups["value"];
                edits.Add(value is null ? (attribute.Index, attribute.Index + attribute.Length, "") : (field.Index, field.Index + field.Length, encoded!));
            }
            else if (value is not null)
            {
                var end = opening.Length - (span.Empty ? 2 : 1);
                edits.Add((end, end, " " + name + "=\"" + encoded + "\""));
            }
        }
        foreach (var edit in edits.OrderByDescending(e => e.Start)) opening = opening[..edit.Start] + edit.Text + opening[edit.End..];
        return new(span.Start, span.OpenEnd, opening);
    }
}

public sealed partial class ProjectService
{
    private (ProjectInfo Project, byte[] Bytes) PreservationSource(MergeSnapshot snapshot)
    {
        var project = Get(snapshot.ProjectId); Expect(project, snapshot.Sha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select an exact managed Logix snapshot.");
        return (project, File.ReadAllBytes(RevisionFile(project.Id, project.Revision)));
    }
    private (LogicValueMergeReport Report, byte[] Bytes) BuildFreshPreservationMerge(LogicValueMergeRequest request)
    {
        if (request.Tags is not null || request.Resolutions is { Length: > 0 }) throw new OperationException("merge_policy", "Preserve-fresh covers the complete saved project; do not combine it with legacy tag maps/resolutions.");
        var choices = request.ConfigurationFields ?? []; LogixPreservation.ValidateChoices(choices);
        var a = PreservationSource(request.Baseline); var b = PreservationSource(request.Logic); var c = PreservationSource(request.Values);
        var baseline = new LogixLexicalDocument(a.Bytes); var logic = new LogixLexicalDocument(b.Bytes); var fresh = new LogixLexicalDocument(c.Bytes);
        var findings = new List<MergeFieldFinding>(); var patches = new List<LogixLexicalDocument.Patch>();
        void Finding(string kind, string location, string status, XElement? old, XElement? reviewed, XElement? current, string detail) => findings.Add(new(kind, location, status,
            LogixPreservation.Fingerprint(old), LogixPreservation.Fingerprint(reviewed), LogixPreservation.Fingerprint(current), detail));
        var at = LogixPreservation.Tags(baseline.Document); var bt = LogixPreservation.Tags(logic.Document); var ct = LogixPreservation.Tags(fresh.Document);
        foreach (var key in at.Keys.Union(bt.Keys, StringComparer.OrdinalIgnoreCase).Union(ct.Keys, StringComparer.OrdinalIgnoreCase))
        {
            at.TryGetValue(key, out var old); bt.TryGetValue(key, out var reviewed); ct.TryGetValue(key, out var current);
            if (LogixPreservation.TagContract(old) != LogixPreservation.TagContract(reviewed))
                Finding("tag-contract", key, "unsupported", old, reviewed, current, "Reviewed tag additions/removals, types, dimensions, aliases or safety class changed. This routine-preservation profile does not transfer declarations.");
            else if (old is not null && LogixPreservation.TagContract(old) != LogixPreservation.TagContract(current))
                Finding("tag-contract", key, "conflict", old, reviewed, current, "The fresh tag declaration/layout differs from the common baseline; review a compatible set of snapshots.");
            else if (old is not null && reviewed is not null && current is not null &&
                (LogixPreservation.Fingerprint(LogixPreservation.Declaration(old)) != LogixPreservation.Fingerprint(LogixPreservation.Declaration(reviewed)) ||
                 LogixPreservation.Fingerprint(LogixPreservation.Declaration(old)) != LogixPreservation.Fingerprint(LogixPreservation.Declaration(current))))
                Finding("tag-configuration", key, "preserved-fresh", LogixPreservation.Declaration(old), LogixPreservation.Declaration(reviewed), LogixPreservation.Declaration(current), "Fresh descriptions and non-layout tag configuration remain authoritative; reviewed tag text/configuration is not transferred by this profile.");
        }
        // Detect unsupported reviewed controller/program/AOI/layout edits without
        // treating independently saved values or fresh native configuration as logic.
        static XElement Header(XElement node, params string[] excluded) => new(node.Name,
            node.Attributes().Where(a => !(node.Name == "Controller" && a.Name == "LastModifiedDate") && !(node.Name == "RSLogix5000Content" && a.Name == "ExportDate")).Select(a => new XAttribute(a)),
            node.Elements().Where(e => !excluded.Contains(e.Name.LocalName)).Select(e => new XElement(e)));
        var ac = baseline.Document.Root!.Element("Controller")!; var bc = logic.Document.Root!.Element("Controller")!; var cc = fresh.Document.Root!.Element("Controller")!;
        foreach (var (old, reviewed, current, location, excluded) in new[] {
            (baseline.Document.Root!, logic.Document.Root!, fresh.Document.Root!, "Root", new[]{"Controller"}),
            (ac, bc, cc, "Controller", new[]{"Tags","Programs"}) })
            if (LogixPreservation.Fingerprint(Header(old, excluded)) != LogixPreservation.Fingerprint(Header(reviewed, excluded)))
                Finding("configuration", location, "unsupported", Header(old, excluded), Header(reviewed, excluded), Header(current, excluded), "Reviewed non-routine configuration changed; preserve-fresh does not silently transfer controller, module, task, UDT, AOI or safety changes.");
        var ap = LogixPreservation.Index(ac.Elements("Programs").Elements("Program"), p => (string)p.Attribute("Name")!);
        var bp = LogixPreservation.Index(bc.Elements("Programs").Elements("Program"), p => (string)p.Attribute("Name")!);
        var cp = LogixPreservation.Index(cc.Elements("Programs").Elements("Program"), p => (string)p.Attribute("Name")!);
        foreach (var key in ap.Keys.Union(bp.Keys, StringComparer.OrdinalIgnoreCase))
        {
            ap.TryGetValue(key, out var old); bp.TryGetValue(key, out var reviewed); cp.TryGetValue(key, out var current);
            if (old is null || reviewed is null || current is null || LogixPreservation.Fingerprint(Header(old, "Tags", "Routines")) != LogixPreservation.Fingerprint(Header(reviewed, "Tags", "Routines")))
                Finding("program-contract", key, "unsupported", old is null ? null : Header(old, "Tags", "Routines"), reviewed is null ? null : Header(reviewed, "Tags", "Routines"), current is null ? null : Header(current, "Tags", "Routines"), "Program identities, metadata and main-routine configuration require a separate reviewed edit.");
        }
        foreach (var section in new[] { "DataTypes", "AddOnInstructionDefinitions" })
        {
            var old = LogixPreservation.LayoutContract(ac, section); var current = LogixPreservation.LayoutContract(cc, section);
            if (LogixPreservation.Fingerprint(old) != LogixPreservation.Fingerprint(current))
                Finding("layout", section, "conflict", old, null, current, "Fresh UDT/AOI declarations changed relative to the common baseline. Saved values are not copied across unknown layouts.");
        }
        var ar = LogixPreservation.Routines(baseline.Document); var br = LogixPreservation.Routines(logic.Document); var cr = LogixPreservation.Routines(fresh.Document);
        var additions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); var replaced = 0; var added = 0; var removed = 0;
        foreach (var key in ar.Keys.Union(br.Keys, StringComparer.OrdinalIgnoreCase).Union(cr.Keys, StringComparer.OrdinalIgnoreCase))
        {
            ar.TryGetValue(key, out var old); br.TryGetValue(key, out var reviewed); cr.TryGetValue(key, out var current);
            var ah = LogixPreservation.Fingerprint(old); var bh = LogixPreservation.Fingerprint(reviewed); var ch = LogixPreservation.Fingerprint(current);
            if (bh == ch) continue;
            if (ah != ch) { Finding("routine", key, "conflict", old, reviewed, current, "Fresh code/annotations differ from both the baseline and reviewed routine; no overwrite or implicit branch merge."); continue; }
            var destinationProgram = (string?)(reviewed ?? current ?? old)?.Ancestors("Program").FirstOrDefault()?.Attribute("Name");
            if ((old is not null && LogixPreservation.Safety(old)) || (reviewed is not null && LogixPreservation.Safety(reviewed)) ||
                (current is not null && LogixPreservation.Safety(current)) || (destinationProgram is not null && cp.TryGetValue(destinationProgram, out var destination) && LogixPreservation.Safety(destination)))
            { Finding("routine", key, "unsupported", old, reviewed, current, "Safety routine changes are outside this offline profile."); continue; }
            if (current is not null)
            {
                var span = fresh.Locate(current); patches.Add(new(span.Start, span.End, reviewed is null ? "" : logic.Raw(reviewed)));
                if (reviewed is null) removed++; else replaced++;
            }
            else if (reviewed is not null)
            {
                var program = (string)reviewed.Parent!.Parent!.Attribute("Name")!;
                if (!cp.ContainsKey(program)) { Finding("routine", key, "conflict", old, reviewed, current, "The destination program is absent from the fresh snapshot."); continue; }
                if (!additions.TryGetValue(program, out var group)) additions.Add(program, group = []);
                group.Add(logic.Raw(reviewed)); added++;
            }
            Finding("routine", key, "reviewed-" + (reviewed is null ? "removed" : old is null ? "added" : "modified"), old, reviewed, current, "Whole reviewed routine and annotations; no rung/value rewriting.");
        }
        foreach (var (program, fragments) in additions)
        {
            var owner = cp[program]; var container = owner.Element("Routines"); var text = string.Join("", fragments);
            if (container is null)
            {
                var span = fresh.Locate(owner); if (span.Empty) { Finding("routine-container", program, "unsupported", null, null, owner, "An empty program container requires a separate edit."); continue; }
                patches.Add(new(span.CloseStart, span.CloseStart, "<Routines>" + text + "</Routines>"));
            }
            else
            {
                var span = fresh.Locate(container);
                if (span.Empty) patches.Add(new(span.Start, span.End, fresh.Text[span.Start..(span.OpenEnd - 2)] + ">" + text + "</Routines>"));
                else patches.Add(new(span.CloseStart, span.CloseStart, text));
            }
        }
        var selected = choices.ToDictionary(c => LogixPreservation.Key(c.ProgramName, c.TagName).ToUpperInvariant() + "/" + c.Attribute, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal); var messageChanges = new Dictionary<XElement, Dictionary<string, string?>>(); var changedFields = 0;
        foreach (var key in at.Keys.Union(bt.Keys, StringComparer.OrdinalIgnoreCase).Union(ct.Keys, StringComparer.OrdinalIgnoreCase))
        {
            at.TryGetValue(key, out var old); bt.TryGetValue(key, out var reviewed); ct.TryGetValue(key, out var current);
            var am = LogixPreservation.Message(old); var bm = LogixPreservation.Message(reviewed); var cm = LogixPreservation.Message(current);
            foreach (var name in new[] { am, bm, cm }.Where(n => n is not null).SelectMany(n => n!.Attributes()).Select(a => a.Name.LocalName).Distinct(StringComparer.Ordinal))
            {
                var av = (string?)am?.Attribute(name); var bv = (string?)bm?.Attribute(name); var cv = (string?)cm?.Attribute(name); var identity = key.ToUpperInvariant() + "/" + name;
                if (new[] { av, bv, cv }.Any(v => v?.Length > 4096)) throw new OperationException("merge_limit", "MESSAGE field values exceed the 4096-character preview budget.");
                selected.TryGetValue(identity, out var choice); if (choice is not null) used.Add(identity);
                if (av == bv && av == cv && choice is null) continue;
                var status = "preserved-fresh"; var detail = "Unselected MESSAGE configuration remains exactly fresh.";
                if (choice is { Choice: "logic" })
                {
                    if (bm is null || cm is null || current is null || (string?)current.Attribute("DataType") != "MESSAGE" || LogixPreservation.Safety(current))
                    { status = "unsupported"; detail = "An explicit MESSAGE field needs compatible existing standard MESSAGE records."; }
                    else
                    {
                        status = "resolved-logic"; detail = "Explicit field exception takes only this reviewed attribute; all other native fields/data remain fresh.";
                        if (bv != cv) { if (!messageChanges.TryGetValue(cm, out var edits)) messageChanges.Add(cm, edits = new(StringComparer.Ordinal)); edits.Add(name, bv); changedFields++; }
                    }
                }
                else if (av != bv && bv != cv && choice is null) { status = "conflict"; detail = "Reviewed MESSAGE configuration changed: choose logic or fresh for this exact field, then preview again."; }
                findings.Add(new("message-field", key + "/MessageParameters/@" + name, status, LogixPreservation.TextHash(av), LogixPreservation.TextHash(bv), LogixPreservation.TextHash(cv), detail, av, bv, cv));
            }
        }
        foreach (var unused in selected.Keys.Except(used)) throw new OperationException("merge_policy", "A MESSAGE field choice is absent from the pinned inputs: " + unused);
        foreach (var (node, fields) in messageChanges) patches.Add(LogixPreservation.MessagePatch(fresh, node, fields));
        if (findings.Count > 10000) throw new OperationException("merge_limit", "Merge findings exceed 10000; use smaller explicit project snapshots.");
        var representations = new Dictionary<string, int>(StringComparer.Ordinal); var changedRepresentations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, tag) in ct)
            foreach (var data in tag.Elements("Data"))
            {
                var format = (string?)data.Attribute("Format") ?? "unknown"; representations[format] = representations.GetValueOrDefault(format) + 1;
                bt.TryGetValue(key, out var reviewed);
                if (LogixPreservation.Fingerprint(data) != LogixPreservation.Fingerprint(reviewed?.Elements("Data").SingleOrDefault(d => (string?)d.Attribute("Format") == format)))
                    changedRepresentations[format] = changedRepresentations.GetValueOrDefault(format) + 1;
            }
        var bytes = fresh.Apply(patches); var resultHash = Hash(bytes);
        var receipt = new MergePreservationReceipt(ct.Count, br.Count, replaced, added, removed, changedFields, representations, changedRepresentations, true, "Program routines plus exact MESSAGE attributes only; every other fresh source byte is retained.");
        var reviewHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, findings, receipt, resultHash }, Json));
        var conflicts = findings.Where(f => f.Status is "conflict" or "unsupported").Select(f => f.Kind + ":" + f.Location + ": " + f.Detail).ToArray();
        static ComparisonSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.Name, p.Revision, p.CurrentSha256);
        var report = new LogicValueMergeReport(Snapshot(a.Project), Snapshot(b.Project), Snapshot(c.Project), reviewHash, resultHash, conflicts.Length == 0, 0, [], conflicts, LogixPreservation.Boundary, findings.ToArray(), receipt, LogixPreservation.PolicyHash(request));
        if (JsonSerializer.SerializeToUtf8Bytes(report, Json).Length > 4 * 1024 * 1024) throw new OperationException("merge_limit", "Merge preview exceeds 4 MiB; no partial report was produced.");
        return (report, bytes);
    }
}
