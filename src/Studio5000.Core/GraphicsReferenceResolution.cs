using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record GraphicsShortcutBinding(string Shortcut, string ProjectId, string ExpectedSha256);
public sealed record GraphicsParameterBinding(string ScopeXPath, string Parameter, string Value);
public sealed record GraphicsResolveRequest(string ExpectedSha256, GraphicsShortcutBinding[] Shortcuts,
    GraphicsParameterBinding[]? Parameters = null, string? Query = null, int Offset = 0, int Limit = 100);
public sealed record GraphicsPlcSnapshot(ProjectInfo Project, XDocument Document);
public sealed record GraphicsResolvedReference(GraphicsTagReference Source, string ExpandedReference, string Status,
    string[] ParameterTrace, string? Shortcut = null, string? ProjectId = null, string? ProjectSha256 = null,
    string? ProgramName = null, string? TagName = null, string? MemberPath = null, string? DataType = null, int[]? Dimensions = null);
public sealed record GraphicsResolutionReport(ProjectInfo Project, GraphicsShortcutBinding[] Shortcuts, int Total, int Offset,
    int Limit, GraphicsResolvedReference[] References, string[] ParameterScopes, string[] Diagnostics, bool DiagnosticsTruncated, string Coverage);

/// <summary>Explicit, snapshot-only context. Never infers a shortcut target or runtime parameter scope.</summary>
public static partial class GraphicsReferenceResolution
{
    [GeneratedRegex(@"#[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
    [GeneratedRegex(@"^#[0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex Parameter();

    public static void Validate(GraphicsResolveRequest request)
    {
        if (request.Shortcuts is null || request.Shortcuts.Length > 64 || request.Parameters?.Length > 256 ||
            request.Offset is < 0 or > 100000 || request.Limit is < 1 or > 200 || request.Query?.Length > 256)
            throw new OperationException("invalid_resolution_context", "Use up to 64 shortcuts, 256 scoped parameters, offset 0..100000, limit 1..200 and query up to 256 characters.");
        var shortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in request.Shortcuts)
            if (binding is null || string.IsNullOrWhiteSpace(binding.Shortcut) || binding.Shortcut.Length > 128 ||
                binding.Shortcut.Any(c => char.IsControl(c) || c is '[' or ']' or '#' or '{' or '}') ||
                string.IsNullOrWhiteSpace(binding.ProjectId) || string.IsNullOrWhiteSpace(binding.ExpectedSha256) || !shortcuts.Add(binding.Shortcut))
                throw new OperationException("invalid_resolution_context", "Provide distinct shortcut names and hash-bound Logix project IDs.");
        var parameters = new HashSet<(string, string)>();
        foreach (var binding in request.Parameters ?? [])
            if (binding is null || string.IsNullOrEmpty(binding.ScopeXPath) || binding.ScopeXPath.Length > 8192 ||
                binding.Parameter is null || !Parameter().IsMatch(binding.Parameter) || binding.Value is null || binding.Value.Length > 1024 ||
                !parameters.Add((binding.ScopeXPath, binding.Parameter)))
                throw new OperationException("invalid_resolution_context", "Parameters require a unique exact scope selector, #number name and value up to 1024 characters.");
    }

    public static GraphicsResolutionReport Resolve(ProjectInfo project, XDocument document, GraphicsResolveRequest request,
        IReadOnlyDictionary<string, GraphicsPlcSnapshot> snapshots)
    {
        Validate(request);
        // Canonical selectors only: no executable/user-supplied XPath or accidental multi-object scopes.
        var scopes = document.Descendants().Where(e => e == document.Root || e.Attribute("name") is not null &&
            e.Name.LocalName is not ("connection" or "parameter")).ToDictionary(FactoryTalkGraphics.StablePath, StringComparer.Ordinal);
        var parameters = new Dictionary<(string, string), string>();
        foreach (var binding in request.Parameters ?? [])
        {
            if (!scopes.ContainsKey(binding.ScopeXPath))
                throw new OperationException("parameter_scope_missing", "Use an exact canonical object/group selector from the registered display.");
            parameters.Add((binding.ScopeXPath, binding.Parameter), binding.Value);
        }
        var projectIndexes = snapshots.Values.DistinctBy(s => s.Project.Id).ToDictionary(s => s.Project.Id, s => new LogixReferenceIndex(s));
        var indexes = snapshots.ToDictionary(p => p.Key, p => projectIndexes[p.Value.Project.Id], StringComparer.OrdinalIgnoreCase);
        var found = new List<GraphicsResolvedReference>();
        var scan = GraphicsReferences.Scan(document);
        foreach (var source in scan.References)
        {
            var trace = new List<string>(); var steps = 0; string? failure = null;
            var ancestry = scopes.TryGetValue(source.ObjectXPath, out var owner)
                ? owner.AncestorsAndSelf().Select(FactoryTalkGraphics.StablePath).ToArray() : [];
            string Expand(string text, HashSet<(string, string)> active, int depth)
            {
                if (depth > 16 || ++steps > 64) { failure = "parameter_expansion_limit"; return text; }
                text = Unwrap(text);
                var expanded = Tokens().Replace(text, match =>
                {
                    if (failure is not null) return match.Value;
                    var scope = ancestry.FirstOrDefault(a => parameters.ContainsKey((a, match.Value)));
                    if (scope is null) { failure = "requires_parameter_context"; return match.Value; }
                    var key = (scope, match.Value);
                    if (!active.Add(key)) { failure = "parameter_cycle"; return match.Value; }
                    trace.Add(scope + ":" + match.Value);
                    var value = Expand(parameters[key], active, depth + 1); active.Remove(key); return value;
                });
                if (expanded.Length > 4096) { failure = "parameter_expansion_limit"; return expanded[..4096]; }
                return expanded;
            }
            var expanded = Expand(source.Reference, [], 0);
            var result = new GraphicsResolvedReference(source, expanded, failure ?? "requires_hmi_database", trace.ToArray());
            if (failure is null && expanded.Contains('#')) result = result with { Status = "unsupported_parameter_syntax" };
            else if (failure is null)
            {
                var qualified = expanded.StartsWith("::", StringComparison.Ordinal) ? expanded[2..] : expanded;
                if (qualified.StartsWith('['))
                {
                    var end = qualified.IndexOf(']');
                    if (end <= 1 || end == qualified.Length - 1) result = result with { Status = "invalid_shortcut_syntax" };
                    else
                    {
                        var shortcut = qualified[1..end]; result = result with { Shortcut = shortcut, Status = "requires_shortcut_mapping" };
                        if (indexes.TryGetValue(shortcut, out var index)) result = index.Resolve(result, qualified[(end + 1)..]);
                    }
                }
                else if (qualified.Contains("::", StringComparison.Ordinal)) result = result with { Status = "requires_qualified_context" };
            }
            if (string.IsNullOrEmpty(request.Query) || result.Source.Reference.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ||
                result.ExpandedReference.Contains(request.Query, StringComparison.OrdinalIgnoreCase) || result.Status.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ||
                result.Source.ObjectName?.Contains(request.Query, StringComparison.OrdinalIgnoreCase) == true) found.Add(result);
        }
        var page = found.Skip(request.Offset).Take(request.Limit).ToArray();
        var pageScopes = page.SelectMany(r => scopes.TryGetValue(r.Source.ObjectXPath, out var owner)
            ? owner.AncestorsAndSelf().Where(e => scopes.ContainsKey(FactoryTalkGraphics.StablePath(e))).Select(FactoryTalkGraphics.StablePath) : [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var report = new GraphicsResolutionReport(project, request.Shortcuts, found.Count, request.Offset, request.Limit,
            page, pageScopes, scan.Diagnostics, scan.Truncated,
            "Explicit shortcut mappings and nearest-ancestor scoped literal parameters only; export parameter values are not inferred. Case-insensitive controller/program tags, declared UDT members, numeric array indices and integer bits are checked against saved L5X metadata. Aliases, absent built-in/AOI/module templates, HMI databases, dynamic expressions and runtime access remain unresolved. Absence means absent in this snapshot, which may be partial. No controller connection, native validation or expression compilation.");
        if (JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json).Length > 4 * 1024 * 1024)
            throw new OperationException("reference_page_too_large", "Resolution page exceeds 4 MiB; request a smaller limit.");
        return report;
    }

    private static string Unwrap(string value)
    {
        value = value.Trim();
        return value.StartsWith('{') && value.EndsWith('}') && !value[1..^1].Contains('{') && !value[1..^1].Contains('}') ? value[1..^1].Trim() : value;
    }
}

internal sealed partial class LogixReferenceIndex
{
    private readonly ProjectInfo project;
    private readonly Dictionary<string, (string? Name, Dictionary<string, XElement> Tags)> scopes;
    private readonly Dictionary<string, StructureType> types;
    private static readonly Dictionary<string, int> IntegerBits = new(StringComparer.OrdinalIgnoreCase)
        { ["SINT"] = 8, ["INT"] = 16, ["DINT"] = 32, ["LINT"] = 64, ["USINT"] = 8, ["UINT"] = 16, ["UDINT"] = 32, ["ULINT"] = 64 };
    [GeneratedRegex(@"^(?:Program:(?<program>[A-Za-z_][A-Za-z0-9_]*)\.)?(?<tag>[A-Za-z_][A-Za-z0-9_]*)(?<tail>(?:\.(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+)|\[[0-9]+(?:,[0-9]+){0,2}\])*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Address();
    [GeneratedRegex(@"\.[A-Za-z_][A-Za-z0-9_]*|\.[0-9]+|\[[0-9]+(?:,[0-9]+){0,2}\]", RegexOptions.CultureInvariant)]
    private static partial Regex Parts();

    public LogixReferenceIndex(GraphicsPlcSnapshot snapshot)
    {
        project = snapshot.Project;
        var controller = snapshot.Document.Root!.Element("Controller")!;
        static Dictionary<string, XElement> Tags(XElement scope) => scope.Elements("Tags").Elements("Tag").ToDictionary(t => (string)t.Attribute("Name")!, StringComparer.OrdinalIgnoreCase);
        scopes = new(StringComparer.OrdinalIgnoreCase) { [""] = (null, Tags(controller)) };
        foreach (var program in controller.Elements("Programs").Elements("Program"))
        {
            var name = (string)program.Attribute("Name")!; scopes.Add(name, (name, Tags(program)));
        }
        types = LogixStructures.Types(snapshot.Document).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }
    public GraphicsResolvedReference Resolve(GraphicsResolvedReference result, string address)
    {
        result = result with { ProjectId = project.Id, ProjectSha256 = project.CurrentSha256 };
        var match = Address().Match(address);
        if (!match.Success) return result with { Status = "unsupported_logix_address" };
        var program = match.Groups["program"].Success ? match.Groups["program"].Value : null;
        var tagName = match.Groups["tag"].Value; var tail = match.Groups["tail"].Value;
        result = result with { ProgramName = program, TagName = tagName, MemberPath = tail.Length == 0 ? null : tail.TrimStart('.') };
        if (!scopes.TryGetValue(program ?? "", out var scope)) return result with { Status = "program_absent_in_snapshot" };
        if (!scope.Tags.TryGetValue(tagName, out var tag)) return result with { Status = "tag_absent_in_snapshot" };
        result = result with { ProgramName = scope.Name, TagName = (string?)tag.Attribute("Name") };
        if (Same((string?)tag.Attribute("TagType"), "Alias") || tag.Attribute("AliasFor") is not null)
            return result with { Status = "alias_requires_resolution" };
        var type = (string?)tag.Attribute("DataType"); var dimensions = LogixStructures.Dimensions((string?)tag.Attribute("Dimensions"));
        foreach (Match part in Parts().Matches(tail))
        {
            if (part.Value[0] == '[')
            {
                var indices = part.Value[1..^1].Split(',');
                if (indices.Length != dimensions.Length) return result with { Status = "array_rank_mismatch" };
                for (var i = 0; i < indices.Length; i++)
                    if (!int.TryParse(indices[i], out var index) || index >= dimensions[i]) return result with { Status = "index_out_of_range" };
                dimensions = []; continue;
            }
            if (dimensions.Length > 0) return result with { Status = "array_index_required" };
            var name = part.Value[1..];
            if (char.IsDigit(name[0]))
            {
                if (type is null || !IntegerBits.TryGetValue(type, out var width)) return result with { Status = "unsupported_bit_selector" };
                if (!int.TryParse(name, out var bit) || bit >= width) return result with { Status = "bit_out_of_range" };
                type = "BOOL"; continue;
            }
            if (type is null || !types.TryGetValue(type, out var declaration)) return result with { Status = "type_metadata_unavailable" };
            var member = declaration.Members.SingleOrDefault(m => Same(m.Name, name));
            if (member is null) return result with { Status = "member_absent_in_snapshot" };
            if (member.Hidden) return result with { Status = "member_not_exposed" };
            type = member.DataType; dimensions = member.Dimensions;
        }
        return result with { Status = type is null ? "type_metadata_unavailable" : "matched_in_snapshot", DataType = type, Dimensions = dimensions };
    }
    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
