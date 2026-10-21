using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record TagRenameDecision(string ReferenceId, string Choice, string Reason);
public sealed record ControllerTagRenameRequest(string ProjectId, string ExpectedSha256, string TagName, string NewName,
    string SafetyHandling = "block", TagRenameDecision[]? Decisions = null);
public sealed record TagRenameReference(string Id, string Kind, string Location, int Start, int Length, string Operand,
    string Action, bool Safety, string SourceValueSha256, string Detail);
public sealed record TagRenameFinding(string Status, string Location, string Detail);
public sealed record ControllerTagRenamePlan(ControllerTagRenameRequest Request, ComparisonSnapshot Source, string DeclarationClass,
    bool CanApply, bool ReviewOnly, string? ResultSha256, string ReviewSha256, TagRenameReference[] References,
    TagRenameFinding[] Findings, Dictionary<string, int> PreservedRepresentations, string Boundary);
public sealed record ControllerTagRenameApplyRequest(ControllerTagRenameRequest Preview, string ExpectedReviewSha256);
public sealed record ControllerTagRenameResult(ProjectInfo Project, Artifact Receipt, string ReviewSha256, bool ReviewOnly);

internal static class ControllerTagRenaming
{
    public const string Boundary = "Offline controller-tag rename to a separate managed project. Exact lexical RLL/ST base identifiers, scoped aliases and simple SafetyTagMap pairs are reviewed; program locals and explicit Program: addresses retain their scopes. Source bytes outside selected identifiers and all saved tag representations are preserved. Unknown operands need exact reasoned decisions; unsupported visible XML references block apply. AOI internals, protected payload semantics, external/HMI/remote references and native compiler/runtime binding are not established. Safety changes require explicit review-only handling, with all signatures/configuration retained and no safety acceptance claim. No SDK job or controller connection.";
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private sealed class Call(string? name, int brackets) { public string? Name = name; public int Argument; public int Brackets = brackets; }
    private static string Raw(XmlSourceDocument source, XElement element) { var span = source.Locate(element); return source.Text[span.Start..span.End]; }
    private static readonly HashSet<string> DataInstructions = new(StringComparer.OrdinalIgnoreCase)
    { "XIC", "XIO", "OTE", "OTL", "OTU", "ONS", "OSR", "OSF", "MOV", "MVM", "ADD", "SUB", "MUL", "DIV", "MOD", "CPT", "NEG", "ABS", "SQRT", "SIN", "COS", "TAN", "LN", "LOG", "EXP", "TRN", "AND", "OR", "XOR", "NOT", "EQU", "NEQ", "GRT", "GEQ", "LES", "LEQ", "LIM", "MEQ", "TON", "TOF", "RTO", "RES", "CTU", "CTD", "COP", "CPS", "FLL", "CLR", "SIZE", "LEN", "CONCAT", "MID", "DELETE", "INSERT", "FIND", "MSG", "PID", "PIDE", "SBR", "RET", "JSR" };
    internal static (ControllerTagRenamePlan Plan, byte[] Bytes) Build(ProjectInfo project, byte[] bytes, ControllerTagRenameRequest request)
    {
        WatchValidation.Identifier(request.TagName); WatchValidation.Identifier(request.NewName);
        if (new[] { "TRUE", "FALSE", "AND", "OR", "XOR", "NOT", "MOD", "IF", "THEN", "ELSE", "ELSIF", "END_IF", "FOR", "TO", "BY", "DO", "END_FOR", "WHILE", "END_WHILE", "REPEAT", "UNTIL", "END_REPEAT", "CASE", "OF", "END_CASE", "RETURN", "EXIT" }.Contains(request.NewName, StringComparer.OrdinalIgnoreCase))
            throw new OperationException("tag_rename_policy", "The destination cannot be a reserved logic keyword.");
        if (request.NewName.Length > 40 || request.TagName == request.NewName || request.SafetyHandling is not ("block" or "review-only"))
            throw new OperationException("tag_rename_policy", "Choose a different Logix identifier up to 40 characters and safetyHandling block or review-only.");
        var decisions = request.Decisions ?? [];
        if (decisions.Length > 1000 || decisions.Any(d => d is null || d.Choice is not ("rename" or "leave-symbol") || string.IsNullOrWhiteSpace(d.Reason) || d.Reason.Length > 500) || decisions.Select(d => d.ReferenceId).Distinct().Count() != decisions.Length)
            throw new OperationException("tag_rename_policy", "Use at most 1000 unique, exact, reasoned operand decisions.");
        using var stream = new MemoryStream(bytes); using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo); XmlProjects.Validate(doc);
        var source = new XmlSourceDocument(bytes, doc); var controller = doc.Root!.Element("Controller")!;
        var tags = controller.Elements("Tags").Elements("Tag").ToArray();
        var tag = tags.SingleOrDefault(t => Same((string?)t.Attribute("Name"), request.TagName)) ?? throw new OperationException("reference_tag_missing", "Select an existing controller-scope tag.");
        if (tags.Any(t => t != tag && Same((string?)t.Attribute("Name"), request.NewName))) throw new OperationException("tag_rename_collision", "The destination controller tag already exists.");
        var oldName = (string)tag.Attribute("Name")!;
        var word = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(oldName) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var findings = new List<TagRenameFinding>(); var references = new List<TagRenameReference>(); var patches = new List<XmlSourceDocument.Patch>();
        var handled = new HashSet<XObject>(); var usedDecisions = new HashSet<string>(); var editors = new Dictionary<XObject, Func<int, int, string, XmlSourceDocument.Patch>>();
        var programs = controller.Elements("Programs").Elements("Program").ToArray();
        var locals = programs.ToDictionary(p => (string)p.Attribute("Name")!, p => p.Elements("Tags").Elements("Tag").Select(t => (string)t.Attribute("Name")!).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var aois = controller.Elements("AddOnInstructionDefinitions").Elements().Select(a => (string?)a.Attribute("Name")).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var safetyChanged = LogixPreservation.Safety(tag); long characters = 0; var tokenCount = 0;
        void Issue(string status, string location, string detail) { if (findings.Count >= 10000) throw new OperationException("tag_rename_limit", "More than 10000 rename findings."); findings.Add(new(status, location, detail)); }
        void Reference(XObject node, string value, int start, int length, string kind, string action, bool safety, string detail)
        {
            var element = node as XElement ?? ((XAttribute)node).Parent!; var path = XmlProjects.PathOf(element) + (node is XAttribute a ? "/@" + a.Name : "");
            var valueHash = ProjectService.Hash(Encoding.UTF8.GetBytes(value));
            var id = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { path, start, length, valueHash, kind }, ProjectService.Json));
            if (action == "decision-required")
            {
                var decision = decisions.SingleOrDefault(d => d.ReferenceId == id);
                if (decision is null) Issue("blocked", path, "Unknown/symbolic operand needs an exact rename or leave-symbol decision: " + id);
                else { usedDecisions.Add(id); action = decision.Choice; detail += " Reviewed decision: " + decision.Reason; }
            }
            if (action == "rename")
            {
                var program = element.Ancestors("Program").FirstOrDefault();
                if (program is not null && locals[(string)program.Attribute("Name")!].Contains(request.NewName) && !Same(request.NewName, oldName))
                { action = "blocked-capture"; Issue("blocked", path, "The destination name would bind to a program-local tag; choose a noncapturing name."); }
                else
                {
                    try { if (!editors.TryGetValue(node, out var edit)) editors[node] = edit = source.ValueEditor(node); patches.Add(edit(start, length, request.NewName)); safetyChanged |= safety; }
                    catch (OperationException ex) { action = "blocked-source"; Issue("blocked", path, ex.Message); }
                }
            }
            references.Add(new(id, kind, path, start, length, value.Substring(start, length), action, safety, valueHash, detail));
            if (references.Count > 10000) throw new OperationException("tag_rename_limit", "More than 10000 rename reference candidates.");
        }
        var nameAttribute = tag.Attribute("Name")!; handled.Add(nameAttribute);
        Reference(nameAttribute, oldName, 0, oldName.Length, "declaration", "rename", LogixPreservation.Safety(tag), "Controller declaration only; all value representations retained.");
        void Scan(XObject[] nodes, string[] values, string? program, string kind, bool safety)
        {
            foreach (var node in nodes) handled.Add(node);
            var text = string.Join('\n', values); if (text.Length > 1024 * 1024 || (characters += text.Length) > 16 * 1024 * 1024) throw new OperationException("tag_rename_limit", "Use at most 1 MiB per source and 16 MiB of executable/alias text.");
            var path = XmlProjects.PathOf(nodes[0] as XElement ?? ((XAttribute)nodes[0]).Parent!);
            var diagnostics = new List<string>(); var masked = TagReferences.MaskSource(text, diagnostics.Add);
            var tokens = TagReferences.Lex(text, diagnostics.Add).OrderBy(t => t.Start).ToArray(); tokenCount += tokens.Length;
            if (tokenCount > 100000) throw new OperationException("tag_rename_limit", "More than 100000 lexical operands.");
            if (diagnostics.Count != 0 && word.IsMatch(text)) Issue("blocked", path, "Unsupported lexical source: " + string.Join(", ", diagnostics.Distinct()));
            var starts = new int[values.Length]; for (var i = 1; i < starts.Length; i++) starts[i] = starts[i - 1] + values[i - 1].Length + 1;
            var calls = new List<Call>(); var bracketDepth = 0; var cursor = 0;
            foreach (var token in tokens)
            {
                while (cursor < token.Start)
                {
                    var c = masked[cursor];
                    if (c == '[') bracketDepth++; else if (c == ']') bracketDepth--;
                    else if (c == '(')
                    {
                        var end = cursor; while (end > 0 && char.IsWhiteSpace(masked[end - 1])) end--; var begin = end;
                        while (begin > 0 && (char.IsAsciiLetterOrDigit(masked[begin - 1]) || masked[begin - 1] == '_')) begin--;
                        calls.Add(new(begin < end ? text[begin..end].ToUpperInvariant() : null, bracketDepth));
                    }
                    else if (c == ')' && calls.Count > 0) calls.RemoveAt(calls.Count - 1);
                    else if (c == ',' && calls.Count > 0 && calls[^1].Brackets == bracketDepth) calls[^1].Argument++;
                    cursor++;
                }
                if (!Same(token.Name, oldName)) continue;
                var unit = Array.FindLastIndex(starts, s => s <= token.Start); var offset = token.Start - starts[unit];
                if (offset + oldName.Length > values[unit].Length) { Issue("blocked", path, "Cross-line identifier cannot be mapped safely."); continue; }
                string action; string detail;
                var call = calls.LastOrDefault(c => c.Name is not null); var instruction = call?.Name;
                if (token.Program is not null) { action = "program-qualified-preserved"; detail = "Explicit program scope does not select this controller declaration."; }
                else if (program is not null && locals[program].Contains(oldName)) { action = "program-local-preserved"; detail = "Existing program local shadows the controller tag."; }
                else if (kind == "alias") { action = "rename"; detail = "Unqualified scoped AliasFor base identifier."; }
                else if (call?.Argument == 0 && instruction is "JSR" or "JMP" or "LBL" or "EVENT") { action = "symbol-preserved"; detail = "Routine/label/task symbol position, not a tag address."; }
                else if (instruction is "GSV" or "SSV" && call!.Argument < 3) { action = "decision-required"; detail = "System object/class/attribute operand may share tag spelling."; }
                else if (instruction is null or "GSV" or "SSV" || DataInstructions.Contains(instruction) || aois.Contains(instruction)) { action = "rename"; detail = "Exact lexical controller-bound base identifier; members and index expressions retained."; }
                else { action = "decision-required"; detail = "Instruction/function operand semantics are not classified by this workflow."; }
                // Explicit Program: tokens are preserved as a whole; their base starts later.
                if (token.Program is not null)
                    Reference(nodes[unit], values[unit], offset, Math.Min(token.Length, values[unit].Length - offset), kind, action, safety, detail);
                else Reference(nodes[unit], values[unit], offset, oldName.Length, kind, action, safety, detail);
            }
        }
        foreach (var program in programs)
        foreach (var routine in program.Elements("Routines").Elements("Routine"))
        {
            var type = (string?)routine.Attribute("Type"); var scope = (string)program.Attribute("Name")!;
            if (type == "RLL") foreach (var text in routine.Elements("RLLContent").Elements("Rung").Elements("Text")) Scan([text], [text.Value], scope, "RLL", LogixPreservation.Safety(text));
            else if (type == "ST") { var lines = routine.Elements("STContent").Elements("Line").ToArray(); if (lines.Length > 0) Scan(lines, lines.Select(l => l.Value).ToArray(), scope, "ST", LogixPreservation.Safety(routine)); }
        }
        foreach (var scope in new[] { controller }.Concat(programs))
        foreach (var alias in scope.Elements("Tags").Elements("Tag").Attributes("AliasFor")) Scan([alias], [alias.Value], scope == controller ? null : (string?)scope.Attribute("Name"), "alias", LogixPreservation.Safety(alias.Parent!));
        if (controller.Attribute("SafetyTagMap") is { } map)
        {
            handled.Add(map);
            if (word.IsMatch(map.Value))
            {
                if (!Regex.IsMatch(map.Value, @"\A\s*[A-Za-z_][A-Za-z0-9_]*\s*=\s*[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*\s*=\s*[A-Za-z_][A-Za-z0-9_]*)*\s*\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) Issue("blocked", "Controller/@SafetyTagMap", "Only explicit simple controller-tag mapping pairs are supported.");
                else foreach (Match match in word.Matches(map.Value)) Reference(map, map.Value, match.Index, match.Length, "safety-map", "rename", true, "Exact tag in a SafetyTagMap pair; review-only safety handling required.");
            }
        }
        foreach (var element in doc.Descendants())
        {
            if (element.AncestorsAndSelf().Any(e => e.Name.LocalName is "Data" or "DefaultData" or "ForceData" or "Description" or "Comment" or "EncodedData")) continue;
            if (element.AncestorsAndSelf("AddOnInstructionDefinition").Any()) continue; // AOI-local symbols are not controller declarations.
            foreach (var attribute in element.Attributes().Where(a => !handled.Contains(a) && !a.IsNamespaceDeclaration && a.Name.LocalName is not ("Name" or "DataType" or "Type")))
                if (word.IsMatch(attribute.Value)) Issue("blocked", XmlProjects.PathOf(element) + "/@" + attribute.Name, "Visible XML reference outside supported executable/alias/mapping fields requires a separate reviewed edit.");
            if (!element.HasElements && !handled.Contains(element) && word.IsMatch(element.Value)) Issue("blocked", XmlProjects.PathOf(element), "Visible scalar reference outside supported RLL/ST fields is not rewritten.");
        }
        foreach (var decision in decisions) if (!usedDecisions.Contains(decision.ReferenceId)) throw new OperationException("stale_revision", "A rename decision no longer selects one exact ambiguous operand.");
        if (safetyChanged) Issue(request.SafetyHandling == "review-only" ? "review-only" : "blocked", XmlProjects.PathOf(tag), "Safety-class declaration or changed safety reference: explicit safetyHandling=review-only is required; signatures/configuration remain untouched and unvalidated.");
        var result = source.Apply(patches); var after = XmlProjects.Parse(result);
        var renamed = after.Root!.Element("Controller")!.Elements("Tags").Elements("Tag").Single(t => (string?)t.Attribute("Name") == request.NewName);
        string Stored(XElement t) => string.Concat(t.Elements().Where(e => e.Name.LocalName is "Data" or "ForceData").Select(e => e.ToString(SaveOptions.DisableFormatting)));
        if (Stored(tag) != Stored(renamed)) throw new OperationException("tag_rename_preservation", "Stored tag representations changed; no rename was published.");
        var formats = tag.Elements("Data").GroupBy(d => (string?)d.Attribute("Format") ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var canApply = findings.All(f => f.Status != "blocked"); var outputHash = ProjectService.Hash(result);
        var review = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, sourceSha256 = project.CurrentSha256, outputHash, references, findings, toolVersion = WorkspaceIdentity.Version }, ProjectService.Json));
        var plan = new ControllerTagRenamePlan(request, new(project.Id, project.Name, project.Revision, project.CurrentSha256), (string?)tag.Attribute("Class") ?? "Standard", canApply, safetyChanged, canApply ? outputHash : null, review, references.ToArray(), findings.ToArray(), formats, Boundary);
        if (JsonSerializer.SerializeToUtf8Bytes(plan, ProjectService.Json).Length > 8 * 1024 * 1024) throw new OperationException("tag_rename_limit", "Rename report exceeds 8 MiB.");
        return (plan, result);
    }
}

public sealed partial class ProjectService
{
    private (ControllerTagRenamePlan Plan, byte[] Bytes) BuildControllerTagRename(ControllerTagRenameRequest request)
    { var input = PreservationSource(new(request.ProjectId, request.ExpectedSha256)); return ControllerTagRenaming.Build(input.Project, input.Bytes, request); }
    public async Task<ControllerTagRenamePlan> PreviewControllerTagRenameAsync(ControllerTagRenameRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return BuildControllerTagRename(request).Plan; } finally { gate.Release(); } }
    public async Task<ControllerTagRenameResult> ApplyControllerTagRenameAsync(ControllerTagRenameApplyRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var built = BuildControllerTagRename(request.Preview);
            if (!built.Plan.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Rename sources, decisions or findings changed; preview again.");
            if (!built.Plan.CanApply) throw new OperationException("tag_rename_blocked", "Resolve all blocked rename findings before applying.");
            var project = PublishDiagnosticProject(built.Bytes, (built.Plan.ReviewOnly ? "REVIEW ONLY: " : "") + "Controller tag rename", "controller-tag-rename:" + built.Plan.Source.Id);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "controller_tag_rename", toolVersion = WorkspaceIdentity.Version, createdAt = DateTimeOffset.UtcNow, plan = built.Plan, result = project }, Json);
            var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project.json"), bytes);
            var receipt = new Artifact(id, project.Id, "json", Hash(bytes), bytes.Length, ControllerTagRenaming.Boundary); Save(Path.Combine(dir, "artifact.json"), receipt);
            return new(project, receipt, built.Plan.ReviewSha256, built.Plan.ReviewOnly);
        }
        finally { gate.Release(); }
    }
}
