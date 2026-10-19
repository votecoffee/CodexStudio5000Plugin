using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record LogicTraceRequest(string ExpectedSha256, string TagName, string? ProgramName = null, string Direction = "both", int Depth = 3);
public sealed record LogicUsage(string Symbol, string Operand, string Role, string Instruction, string Owner, string RoutineXPath, string SourceXPath, int RungIndex, string[] AliasChain);
public sealed record LogicDependency(string From, string To, string Kind, string SourceXPath);
public sealed record LogicTraceReport(string ProjectId, string ProjectSha256, string Root, string[] Symbols, LogicUsage[] Usages,
    LogicDependency[] Dependencies, string[] Diagnostics, bool Limited, string Boundary);
internal sealed record LogicAddress(string Symbol, string Tag, string? Program, string Member, string[] Aliases, string? Issue = null);

internal sealed class LogicContext(XDocument document)
{
    internal XDocument Document => document;
    internal XElement Controller => document.Root!.Element("Controller")!;
    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal XElement? Tag(string name, string? program)
    {
        var scope = program is null ? Controller : Controller.Elements("Programs").Elements("Program").FirstOrDefault(p => Same((string?)p.Attribute("Name"), program));
        return scope?.Elements("Tags").Elements("Tag").FirstOrDefault(t => Same((string?)t.Attribute("Name"), name));
    }
    internal LogicAddress? Resolve(string text, string? program, Dictionary<string, LogicAddress>? bindings = null, HashSet<string>? active = null)
    {
        var match = Regex.Match(text.Trim(), @"\A(?:(?:Program:)(?<program>[A-Za-z_][A-Za-z0-9_]*)\.)?(?<tag>[A-Za-z_][A-Za-z0-9_]*)(?<member>(?:\.[A-Za-z_0-9]+|\[[^\]]+\])*)\z", RegexOptions.IgnoreCase);
        if (!match.Success || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)) return null;
        var name = match.Groups["tag"].Value; var suffix = match.Groups["member"].Value;
        if (!match.Groups["program"].Success && bindings?.TryGetValue(name, out var binding) == true) return binding with { Member = binding.Member + suffix };
        var scope = match.Groups["program"].Success ? match.Groups["program"].Value : program is not null && Tag(name, program) is not null ? program : null;
        var tag = Tag(name, scope); if (tag is null) return new((scope is null ? "Controller" : "Program:" + scope) + "." + name, name, scope, suffix, [], "declaration_missing");
        name = (string)tag.Attribute("Name")!; var symbol = (scope is null ? "Controller" : "Program:" + scope) + "." + name;
        active ??= new(StringComparer.OrdinalIgnoreCase);
        if (active.Count >= 32 || !active.Add(symbol)) return new(symbol, name, scope, suffix, [symbol], "alias_cycle_or_limit");
        if (tag.Attribute("AliasFor") is not { } alias) return new(symbol, name, scope, suffix, []);
        var resolved = Resolve(alias.Value, scope, bindings, active);
        return resolved is null ? new(symbol, name, scope, suffix, [symbol], "alias_target_unsupported") : resolved with { Member = resolved.Member + suffix, Aliases = new[] { symbol }.Concat(resolved.Aliases).ToArray() };
    }
}

public static class LogicTracing
{
    public const string Boundary = "Conservative saved-project dependency analysis at base-tag granularity. Instruction operands and rung guards describe possible reads/writes, not scan execution. Aliases and unambiguous AOI call parameters are followed; dynamic addressing, source-protected/unsupported languages/instructions and unresolved layouts are explicit diagnostics. Scheduling, indirect routine dispatch, external writers and native compiler binding are not proven.";
    internal static string[] Roles(string instruction, int count)
    {
        var name = instruction.ToUpperInvariant(); var roles = Enumerable.Repeat("read", count).ToArray();
        int[] writes = name switch
        {
            "XIC" or "XIO" or "EQU" or "NEQ" or "GRT" or "GEQ" or "LES" or "LEQ" or "LIM" or "MEQ" => [],
            "OTE" or "OTL" or "OTU" or "RES" or "CPT" => [0], "MOV" or "NOT" => [1],
            "ADD" or "SUB" or "MUL" or "DIV" or "MOD" or "AND" or "OR" or "XOR" => [2],
            "COP" or "CPS" or "FLL" => [1], "TON" or "TOF" or "RTO" or "CTU" or "CTD" => [0],
            "ONS" => [0], "NOP" or "AFI" => [], _ => [-1]
        };
        if (writes.Contains(-1)) return Enumerable.Repeat("unknown", count).ToArray();
        foreach (var i in writes.Where(i => i < count)) roles[i] = name is "TON" or "TOF" or "RTO" or "CTU" or "CTD" or "ONS" ? "read-write" : "write";
        return roles;
    }
    public static LogicTraceReport Trace(ProjectInfo project, XDocument document, LogicTraceRequest request)
    {
        if (request.Depth is < 0 or > 8 || request.Direction is not ("both" or "upstream" or "downstream")) throw new OperationException("logic_trace_query", "Use depth 0..8 and direction both/upstream/downstream.");
        WatchValidation.Identifier(request.TagName); if (request.ProgramName is not null) WatchValidation.Identifier(request.ProgramName);
        ProjectComparison.ValidateSize(document); var context = new LogicContext(document);
        if (context.Tag(request.TagName, request.ProgramName) is null) throw new OperationException("logic_trace_tag", "Select an existing exact tag declaration.");
        var root = context.Resolve((request.ProgramName is null ? "" : "Program:" + request.ProgramName + ".") + request.TagName, null)!;
        var usages = new List<LogicUsage>(); var edges = new List<LogicDependency>(); var issues = new HashSet<string>(StringComparer.Ordinal); var limited = false; var calls = 0; var sourceCharacters = 0;
        void Issue(string message) { if (issues.Count < 100) issues.Add(message); else limited = true; }
        if (root.Issue is not null) Issue(root.Issue + ": " + root.Symbol);
        IEnumerable<LadderPreview.Part> Flatten(IEnumerable<LadderPreview.Part> parts) => parts.SelectMany(p => p.Legs is null ? new[] { p } : p.Legs.SelectMany(Flatten));
        var definitions = context.Controller.Elements("AddOnInstructionDefinitions").Elements("AddOnInstructionDefinition").ToDictionary(a => (string)a.Attribute("Name")!, StringComparer.OrdinalIgnoreCase);
        void Routine(XElement routine, string? program, string owner, Dictionary<string, LogicAddress>? bindings, HashSet<string> active)
        {
            if ((string?)routine.Attribute("Type") != "RLL") { Issue("unsupported_routine_language: " + XmlProjects.PathOf(routine)); return; }
            var index = 0;
            foreach (var rung in routine.Elements("RLLContent").Elements("Rung"))
            {
                var path = XmlProjects.PathOf(rung); var text = (string?)rung.Element("Text") ?? "";
                if ((sourceCharacters += text.Length) > 16 * 1024 * 1024 || ++calls > 10000) throw new OperationException("logic_trace_limit", "Trace exceeds 16 MiB source or 10000 expanded rungs.");
                LadderPreview.Part[] parts;
                try { parts = Flatten(LadderPreview.ParseLogic(text)).ToArray(); }
                catch (OperationException ex) { Issue(ex.Code + ": " + path); index++; continue; }
                var local = new List<LogicUsage>();
                foreach (var part in parts)
                {
                    var roles = Roles(part.Instruction, part.Operands.Length);
                    if (part.Instruction.Equals("JSR", StringComparison.OrdinalIgnoreCase)) { Issue("conditional_routine_call: " + path); continue; }
                    if (definitions.TryGetValue(part.Instruction, out var aoi))
                    {
                        var parameters = aoi.Elements("Parameters").Elements("Parameter").Where(p => (string?)p.Attribute("Name") is not ("EnableIn" or "EnableOut")).ToArray();
                        var visible = parameters.Where(p => (string?)p.Attribute("Required") == "true" || (string?)p.Attribute("Visible") == "true" || (string?)p.Attribute("Usage") == "InOut").ToArray();
                        var required = parameters.Where(p => (string?)p.Attribute("Required") == "true" || (string?)p.Attribute("Usage") == "InOut").ToArray();
                        var layout = part.Operands.Length == visible.Length + 1 ? visible : part.Operands.Length == required.Length + 1 ? required : null;
                        var instance = part.Operands.Length > 0 ? context.Resolve(part.Operands[0], program, bindings) : null;
                        if (layout is null || instance is null || instance.Issue is not null || active.Count >= 8 || !active.Add(part.Instruction))
                        { Issue("aoi_binding_or_recursion_unknown: " + part.Instruction + ": " + path); roles = Enumerable.Repeat("unknown", part.Operands.Length).ToArray(); }
                        else
                        {
                            roles = new[] { "read-write" }.Concat(layout.Select(p => (string?)p.Attribute("Usage") switch { "Input" => "read", "Output" => "write", "InOut" => "read-write", _ => "unknown" })).ToArray();
                            var map = new Dictionary<string, LogicAddress>(StringComparer.OrdinalIgnoreCase);
                            foreach (var tag in aoi.Elements("LocalTags").Elements("LocalTag").Concat(parameters))
                            {
                                var name = (string)tag.Attribute("Name")!; map[name] = instance with { Symbol = instance.Symbol + "." + name, Member = "", Aliases = [] };
                            }
                            foreach (var p in parameters.Where(p => p.Attribute("AliasFor") is not null))
                            {
                                var target = p.Attribute("AliasFor")!.Value.Split('.', 2);
                                if (map.TryGetValue(target[0], out var resolved)) map[(string)p.Attribute("Name")!] = resolved with { Member = target.Length > 1 ? "." + target[1] : "" };
                            }
                            for (var i = 0; i < layout.Length; i++)
                            {
                                var name = (string)layout[i].Attribute("Name")!; var actual = context.Resolve(part.Operands[i + 1], program, bindings);
                                if (actual is null) continue;
                                if ((string?)layout[i].Attribute("Usage") == "InOut") map[name] = actual;
                                else
                                {
                                    var parameter = map[name]; var usage = (string?)layout[i].Attribute("Usage");
                                    edges.Add(usage == "Output" ? new(parameter.Symbol, actual.Symbol, "aoi-output", path) : new(actual.Symbol, parameter.Symbol, "aoi-input", path));
                                }
                            }
                            foreach (var body in aoi.Elements("Routines").Elements("Routine")) Routine(body, program, owner + "/AOI:" + part.Operands[0] + ":" + part.Instruction, map, active);
                            active.Remove(part.Instruction);
                        }
                    }
                    else if (roles.Any(r => r == "unknown")) Issue("instruction_effect_unknown: " + part.Instruction + ": " + path);
                    for (var i = 0; i < part.Operands.Length; i++)
                    {
                        var address = context.Resolve(part.Operands[i], program, bindings);
                        if (address is null) { if (part.Operands[i].Any(char.IsAsciiLetter) && !bool.TryParse(part.Operands[i], out _) && !decimal.TryParse(part.Operands[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) Issue("operand_expression_not_resolved: " + part.Operands[i]); continue; }
                        var role = roles[i];
                        if (address.Issue is not null) Issue(address.Issue + ": " + part.Operands[i]);
                        if (Regex.IsMatch(address.Member, @"\[[^\]]*[A-Za-z_][^\]]*\]")) { role = "unknown"; Issue("dynamic_address_unknown: " + part.Operands[i]); }
                        local.Add(new(address.Symbol, part.Operands[i], role, part.Instruction, owner + "/" + (string?)routine.Attribute("Name"), XmlProjects.PathOf(routine), path, index, address.Aliases));
                    }
                }
                usages.AddRange(local);
                foreach (var read in local.Where(u => u.Role is "read" or "read-write"))
                foreach (var write in local.Where(u => u.Role is "write" or "read-write"))
                    if (!LogicContext.Same(read.Symbol, write.Symbol)) edges.Add(new(read.Symbol, write.Symbol, "possible-rung-dependency", path));
                if (usages.Count > 50000 || edges.Count > 100000) throw new OperationException("logic_trace_limit", "Trace exceeds its usage/dependency budget.");
                index++;
            }
            if (!routine.Elements("RLLContent").Elements("Rung").Any()) Issue("routine_empty_or_source_unavailable: " + XmlProjects.PathOf(routine));
        }
        foreach (var program in context.Controller.Elements("Programs").Elements("Program"))
        foreach (var routine in program.Elements("Routines").Elements("Routine")) Routine(routine, (string?)program.Attribute("Name"), "Program:" + (string?)program.Attribute("Name"), null, new(StringComparer.OrdinalIgnoreCase));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Symbol }; var frontier = new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < request.Depth; depth++)
        {
            var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in edges)
            {
                if (request.Direction != "upstream" && frontier.Contains(edge.From) && !visited.Contains(edge.To)) next.Add(edge.To);
                if (request.Direction != "downstream" && frontier.Contains(edge.To) && !visited.Contains(edge.From)) next.Add(edge.From);
            }
            if (visited.Count + next.Count > 1000) throw new OperationException("logic_trace_limit", "Traversal exceeds 1000 symbols; reduce depth.");
            visited.UnionWith(next); frontier = next;
        }
        var selectedEdges = edges.Distinct().Where(e => visited.Contains(e.From) && visited.Contains(e.To)).ToArray();
        limited |= edges.Any(e => request.Direction != "upstream" && visited.Contains(e.From) && !visited.Contains(e.To) || request.Direction != "downstream" && visited.Contains(e.To) && !visited.Contains(e.From));
        var selectedUsages = usages.Where(u => visited.Contains(u.Symbol)).ToArray();
        var report = new LogicTraceReport(project.Id, project.CurrentSha256, root.Symbol, visited.Order(StringComparer.OrdinalIgnoreCase).ToArray(), selectedUsages, selectedEdges, issues.ToArray(), limited, Boundary);
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json).Length > 4 * 1024 * 1024) throw new OperationException("logic_trace_limit", "Trace response exceeds 4 MiB; reduce depth.");
        return report;
    }
}

public sealed partial class ProjectService
{
    public LogicTraceReport TraceLogic(string id, LogicTraceRequest request)
    {
        var p = Get(id); Expect(p, request.ExpectedSha256); if (p.Kind != "logix") throw new OperationException("not_logix", "Select a Logix snapshot.");
        return LogicTracing.Trace(p, XmlProjects.Parse(File.ReadAllBytes(RevisionFile(p.Id, p.Revision))), request);
    }
}
