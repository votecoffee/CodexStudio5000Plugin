using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record TagReferenceRequest(string ExpectedSha256, string TagName, string? ProgramName = null,
    string? Query = null, int Offset = 0, int Limit = 100);
public sealed record TagReferenceOccurrence(string Kind, string? ProgramName, string OwnerName, string? RoutineType,
    string? RoutineXPath, string SourceXPath, string? Number, int Start, int Length, string Operand, string Role,
    string Status, string Snippet, int SnippetStart, bool SnippetTruncated, int? RungIndex = null);
public sealed record TagReferenceReport(ProjectInfo Project, string TagName, string? ProgramName, int Total,
    int Offset, int Limit, TagReferenceOccurrence[] References, int ScannedSources, int SkippedSources,
    string[] Diagnostics, bool DiagnosticsTruncated, string Coverage);

/// <summary>A lexical where-used view. Compiler binding, instruction effects and alias propagation are deliberately separate.</summary>
public static class TagReferences
{
    private sealed record Token(int Start, int Length, string Name, string? Program, string Role, string Status);
    private sealed record Unit(XElement Element, string Text, int Start, string? Number);
    private static bool Word(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    private static bool Initial(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static TagReferenceReport Find(ProjectInfo project, XDocument document, TagReferenceRequest request)
    {
        WatchValidation.Identifier(request.TagName); if (request.ProgramName is not null) WatchValidation.Identifier(request.ProgramName);
        if (request.TagName.Length > 256 || request.ProgramName?.Length > 256 || request.Offset is < 0 or > 100000 ||
            request.Limit is < 1 or > 200 || request.Query?.Length > 256)
            throw new OperationException("invalid_tag_reference_query", "Use exact base tag/program names up to 256 characters, offset 0..100000, limit 1..200 and query up to 256 characters.");
        var controller = document.Root!.Element("Controller")!;
        var programs = controller.Elements("Programs").Elements("Program").ToDictionary(p => (string)p.Attribute("Name")!, StringComparer.OrdinalIgnoreCase);
        var localTags = programs.ToDictionary(p => p.Key, p => p.Value.Elements("Tags").Elements("Tag")
            .Select(t => (string)t.Attribute("Name")!).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var targetScope = request.ProgramName is null ? controller : programs.GetValueOrDefault(request.ProgramName);
        var target = targetScope?.Elements("Tags").Elements("Tag").SingleOrDefault(t => Same((string?)t.Attribute("Name"), request.TagName))
            ?? throw new OperationException("reference_tag_missing", "The selected base tag is absent from this saved snapshot.");
        var tagName = (string)target.Attribute("Name")!; var targetProgram = request.ProgramName is null ? null : (string?)targetScope!.Attribute("Name");
        var results = new List<TagReferenceOccurrence>(); var diagnostics = new List<string>();
        var diagnosticCount = 0; var scanned = 0; var skipped = 0; var tokens = 0; long characters = 0;
        void Diagnose(string message) { diagnosticCount++; if (diagnostics.Count < 50) diagnostics.Add(message); }
        void Scan(Unit[] units, string text, string? program, string owner, string kind, string? type, string? routinePath)
        {
            if (text.Length > 1024 * 1024 || (characters += text.Length) > 16 * 1024 * 1024)
                throw new OperationException("tag_reference_limit", "Use at most 1 MiB of text per routine and 16 MiB across scanned sources.");
            scanned += units.Length; var unitIndex = 0;
            foreach (var token in Lex(text, s => Diagnose(s + ": " + XmlProjects.PathOf(units[0].Element))))
            {
                if (++tokens > 100000) throw new OperationException("tag_reference_limit", "Source exceeds 100000 lexical address candidates.");
                if (!Same(token.Name, tagName)) continue;
                // Program locals shadow controller tags. Explicit Program:Name addresses select that declaration scope.
                var binding = token.Program ?? (program is not null && localTags[program].Contains(token.Name) ? program : null);
                if (!Same(binding, targetProgram)) continue;
                while (unitIndex + 1 < units.Length && token.Start >= units[unitIndex + 1].Start) unitIndex++;
                var unit = units[unitIndex]; var start = token.Start - unit.Start;
                if (start < 0 || start + token.Length > unit.Text.Length) { Diagnose("cross_line_address_not_scanned: " + XmlProjects.PathOf(unit.Element)); continue; }
                var snippetStart = Math.Max(0, start - 120); var snippetEnd = Math.Min(unit.Text.Length, Math.Max(start + token.Length, snippetStart + 512));
                var snippet = unit.Text[snippetStart..Math.Min(snippetEnd, snippetStart + 512)];
                results.Add(new(kind, program, owner, type, routinePath, XmlProjects.PathOf(unit.Element) + (kind == "alias" ? "/@AliasFor" : ""),
                    unit.Number, start, token.Length, unit.Text.Substring(start, token.Length), token.Role, token.Status,
                    snippet, snippetStart, snippetStart != 0 || snippet.Length != unit.Text.Length,
                    type == "RLL" ? unit.Element.Parent!.ElementsBeforeSelf("Rung").Count() : null));
                if (results.Count > 10000) throw new OperationException("tag_reference_limit", "A tag exceeds 10000 candidate occurrences.");
            }
        }
        foreach (var (program, element) in programs)
        foreach (var routine in element.Elements("Routines").Elements("Routine"))
        {
            var type = (string?)routine.Attribute("Type"); var name = (string?)routine.Attribute("Name") ?? "";
            if (type == "RLL")
            {
                foreach (var rung in routine.Elements("RLLContent").Elements("Rung"))
                {
                    if (rung.Element("Text") is not { } text) { skipped++; Diagnose("missing_rung_text: " + XmlProjects.PathOf(rung)); continue; }
                    if (text.Value.Length > 65536) throw new OperationException("tag_reference_limit", "Rung text exceeds 64 KiB.");
                    Scan([new(text, text.Value, 0, (string?)rung.Attribute("Number"))], text.Value, program, name, "routine", type, XmlProjects.PathOf(routine));
                }
                if (!routine.Elements("RLLContent").Elements("Rung").Any()) { skipped++; Diagnose("missing_routine_text: " + XmlProjects.PathOf(routine)); }
            }
            else if (type == "ST")
            {
                var start = 0; var units = routine.Elements("STContent").Elements("Line").Select(line =>
                { var unit = new Unit(line, line.Value, start, (string?)line.Attribute("Number")); start = checked(start + line.Value.Length + 1); return unit; }).ToArray();
                if (units.Length != 0) Scan(units, string.Join('\n', units.Select(u => u.Text)), program, name, "routine", type, XmlProjects.PathOf(routine));
                else { skipped++; Diagnose("missing_routine_text: " + XmlProjects.PathOf(routine)); }
            }
            else { skipped++; Diagnose("unsupported_routine_type: " + XmlProjects.PathOf(routine)); }
        }
        foreach (var routine in controller.Elements("AddOnInstructionDefinitions").Descendants("Routine"))
        { skipped++; Diagnose("aoi_scope_not_scanned: " + XmlProjects.PathOf(routine)); }
        foreach (var scope in new[] { controller }.Concat(programs.Values))
        foreach (var alias in scope.Elements("Tags").Elements("Tag").Where(t => t.Attribute("AliasFor") is not null))
        {
            var text = (string)alias.Attribute("AliasFor")!;
            Scan([new(alias, text, 0, null)], text, scope == controller ? null : (string?)scope.Attribute("Name"), (string?)alias.Attribute("Name") ?? "", "alias", null, null);
        }
        var filtered = results.Where(r => string.IsNullOrEmpty(request.Query) || string.Join(' ', r.Operand, r.ProgramName, r.OwnerName, r.Kind, r.Role, r.Status)
            .Contains(request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var report = new TagReferenceReport(project, tagName, targetProgram, filtered.Length, request.Offset, request.Limit,
            filtered.Skip(request.Offset).Take(request.Limit).ToArray(), scanned, skipped, diagnostics.ToArray(), diagnosticCount > diagnostics.Count,
            "Lexical candidates in saved program RLL/ST text and direct AliasFor declarations. Local declarations shadow controller tags; explicit Program:Name.Tag selects that scope. Comments, quoted strings, numeric literals and call names are skipped. Dynamic index expressions are scanned separately. No compiler binding, read/write classification, type/member/bounds validation, transitive alias propagation, AOI-local scope, FBD/SFC, native where-used equivalence or controller connection. Labels/routine names/symbolic operands may share tag spelling; an empty report does not prove a tag is unused or safe to remove.");
        if (JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json).Length > 4 * 1024 * 1024)
            throw new OperationException("tag_reference_page_too_large", "Response exceeds 4 MiB; use a smaller page.");
        return report;
    }

    private static IEnumerable<Token> Lex(string text, Action<string> diagnose)
    {
        var masked = text.ToCharArray();
        void Mask(int start, int end) { for (var i = start; i < end; i++) if (masked[i] is not ('\r' or '\n')) masked[i] = ' '; }
        for (var i = 0; i < text.Length;)
        {
            var start = i;
            if (text[i] is '\'' or '"')
            {
                var quote = text[i++]; var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '$' && i + 1 < text.Length) { i += 2; continue; }
                    if (text[i++] != quote) continue;
                    if (i < text.Length && text[i] == quote) { i++; continue; }
                    closed = true; break;
                }
                if (!closed) diagnose("unterminated_string"); Mask(start, i); continue;
            }
            if (i + 1 < text.Length && text.AsSpan(i, 2).SequenceEqual("//"))
            { while (i < text.Length && text[i] != '\n') i++; Mask(start, i); continue; }
            if (i + 1 < text.Length && (text.AsSpan(i, 2).SequenceEqual("(*") || text.AsSpan(i, 2).SequenceEqual("/*")))
            {
                var closing = new Stack<string>(); closing.Push(text[i] == '(' ? "*)" : "*/"); i += 2;
                while (i < text.Length && closing.Count > 0)
                {
                    if (i + 1 < text.Length && text.AsSpan(i, 2).SequenceEqual(closing.Peek())) { closing.Pop(); i += 2; }
                    else if (i + 1 < text.Length && (text.AsSpan(i, 2).SequenceEqual("(*") || text.AsSpan(i, 2).SequenceEqual("/*")))
                    {
                        if (closing.Count >= 32) throw new OperationException("tag_reference_limit", "Comment nesting exceeds 32 levels.");
                        closing.Push(text[i] == '(' ? "*)" : "*/"); i += 2;
                    }
                    else i++;
                }
                if (closing.Count > 0) diagnose("unterminated_comment"); Mask(start, i); continue;
            }
            i++;
        }
        return Range(0, masked.Length, "operand", 0);

        IEnumerable<Token> Range(int from, int end, string role, int depth)
        {
            if (depth > 16) throw new OperationException("tag_reference_limit", "Array expression nesting exceeds 16 levels.");
            var position = from;
            string Name() { var start = position++; while (position < end && Word(masked[position])) position++; return text[start..position]; }
            while (position < end)
            {
                if (char.IsAsciiDigit(masked[position]))
                { while (position < end && (Word(masked[position]) || masked[position] is '#' or '.')) position++; continue; }
                if (!Initial(masked[position])) { position++; continue; }
                var start = position; var name = Name(); string? program = null;
                if (start > from && masked[start - 1] is '.' or ':' or '#') continue;
                if (position < end && masked[position] == '#')
                { while (position < end && (Word(masked[position]) || masked[position] is '#' or '.')) position++; continue; }
                if (Same(name, "Program") && position + 1 < end && masked[position] == ':' && Initial(masked[position + 1]))
                {
                    position++; program = Name();
                    if (position + 1 >= end || masked[position] != '.' || !Initial(masked[position + 1])) { diagnose("unsupported_qualified_address"); continue; }
                    position++; name = Name();
                }
                else if (position + 1 < end && masked[position] == ':' && masked[position + 1] != '=')
                {
                    diagnose("unsupported_qualified_address");
                    while (position < end && (Word(masked[position]) || masked[position] is ':' or '.' or '[' or ']')) position++;
                    continue;
                }
                var indices = new List<(int Start, int End)>(); var status = "candidate";
                while (position < end)
                {
                    if (masked[position] == '.' && position + 1 < end && Word(masked[position + 1])) { position++; Name(); }
                    else if (masked[position] == '[')
                    {
                        var indexStart = ++position; var nesting = 1;
                        while (position < end && nesting > 0)
                        {
                            if (masked[position] == '[' && ++nesting > 16) throw new OperationException("tag_reference_limit", "Array expression nesting exceeds 16 levels.");
                            if (masked[position] == ']') nesting--; if (nesting > 0) position++;
                        }
                        if (nesting != 0) { diagnose("unterminated_array_selector"); status = "syntax_unresolved"; break; }
                        indices.Add((indexStart, position));
                        if (masked[indexStart..position].Any(c => !char.IsAsciiDigit(c) && !char.IsWhiteSpace(c) && c != ',')) status = "dynamic_address_candidate";
                        position++;
                    }
                    else break;
                }
                var look = position; while (look < end && char.IsWhiteSpace(masked[look])) look++;
                if (!(look < end && masked[look] == '(') && name.ToUpperInvariant() is not ("TRUE" or "FALSE" or "AND" or "OR" or "NOT" or "XOR" or "MOD"))
                {
                    if (position - start > 4096) throw new OperationException("tag_reference_limit", "Address text exceeds 4096 characters.");
                    yield return new(start, position - start, name, program, role, status);
                }
                foreach (var index in indices)
                foreach (var token in Range(index.Start, index.End, "index_expression", depth + 1)) yield return token;
            }
        }
    }
}

public sealed partial class ProjectService
{
    public TagReferenceReport FindTagReferences(string id, TagReferenceRequest request)
    {
        var project = Get(id); Expect(project, request.ExpectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select a registered Logix snapshot.");
        return TagReferences.Find(project, XmlProjects.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision))), request);
    }
}
