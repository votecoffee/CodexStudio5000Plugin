using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

internal static partial class StructuralEditing
{
    private static void Dependencies(XDocument before, XDocument after, HashSet<string> wholeTags, List<StructuralFinding> findings)
    {
        void Finding(string status, string check, string location, string detail)
        { if (findings.Count >= 2000) throw new OperationException("structural_limit", "More than 2000 dependency findings."); findings.Add(new(status, check, location, detail)); }
        var oldTypes = Types(before); var newTypes = Types(after); var removedTypes = oldTypes.Keys.Except(newTypes.Keys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in after.Descendants().Attributes("DataType").Where(a => removedTypes.Contains(a.Value))) Finding("blocked", "removed-type-reference", XmlProjects.PathOf(attribute.Parent!) + "/@DataType", "Visible declaration/value still refers to removed UDT " + attribute.Value + ".");
        string Shape(XElement type)
        {
            var copy = new XElement(type); copy.Descendants().Where(e => e.Name.LocalName is "Description" or "Comment" or "LocalizedDescription" or "LocalizedComment").Remove();
            return Canonical(copy).ToString(SaveOptions.DisableFormatting);
        }
        var affected = oldTypes.Where(t => newTypes.TryGetValue(t.Key, out var current) && Shape(t.Value) != Shape(current)).Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in oldTypes)
        foreach (var member in type.Elements("Members").Elements("Member"))
        { var dependency = (string?)member.Attribute("DataType"); if (dependency is null) continue; if (!dependents.TryGetValue(dependency, out var list)) dependents[dependency] = list = []; list.Add(name); }
        var pending = new Queue<string>(affected);
        while (pending.TryDequeue(out var name)) foreach (var dependent in dependents.GetValueOrDefault(name) ?? []) if (affected.Add(dependent)) pending.Enqueue(dependent);
        var oldTags = Tags(before); var newTags = Tags(after);
        foreach (var (key, tag) in oldTags)
            if (affected.Contains((string?)tag.Attribute("DataType") ?? "") && newTags.ContainsKey(key) && !wholeTags.Contains(key))
                Finding("blocked", "changed-layout-values", XmlProjects.PathOf(tag), "A changed UDT layout affects this retained tag. Supply an explicitly owned whole-tag replacement with reviewed complete saved representations; values are not repacked or defaulted automatically.");
        foreach (var node in Protected(before))
        foreach (var attribute in node.DescendantsAndSelf().Attributes("DataType").Where(a => affected.Contains(a.Value) || removedTypes.Contains(a.Value)))
            Finding("blocked", "protected-layout-dependency", XmlProjects.PathOf(attribute.Parent!), "Protected library/configuration depends on an edited UDT layout: " + attribute.Value + ".");

        var oldPrograms = before.Root!.Element("Controller")!.Elements("Programs").Elements("Program").ToArray(); var programs = after.Root!.Element("Controller")!.Elements("Programs").Elements("Program").ToArray();
        long characters = 0;
        string Mask(string code)
        {
            if (code.Length > 1024 * 1024 || (characters += code.Length) > 32 * 1024 * 1024) throw new OperationException("structural_limit", "Dependency scanning supports 1 MiB per routine and 32 MiB total source text.");
            return new string(TagReferences.MaskSource(code, _ => { }));
        }
        string Code(XElement routine) => (string?)routine.Attribute("Type") switch
        {
            "RLL" => string.Join('\n', routine.Elements("RLLContent").Elements("Rung").Elements("Text").Select(t => t.Value)),
            "ST" => string.Join('\n', routine.Elements("STContent").Elements("Line").Select(t => t.Value)), _ => ""
        };
        var maskedBefore = oldPrograms.SelectMany(p => p.Elements("Routines").Elements("Routine")).ToDictionary(r => r, r => Mask(Code(r)));
        var maskedAfter = programs.SelectMany(p => p.Elements("Routines").Elements("Routine")).ToDictionary(r => r, r => Mask(Code(r)));
        Dictionary<string, (string Location, string Detail)> RoutineReferences(XElement[] sourcePrograms, Dictionary<XElement, string> masked)
        {
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var program in sourcePrograms)
            {
                var routines = program.Elements("Routines").Elements("Routine").ToArray(); var names = routines.Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase); var main = (string?)program.Attribute("MainRoutineName");
                if (!string.IsNullOrEmpty(main) && !names.Contains(main)) result[Key(Name(program), "main/" + main)] = (XmlProjects.PathOf(program), "Main routine is absent: " + main);
                foreach (var routine in routines)
                foreach (Match match in Regex.Matches(masked[routine], @"(?<![A-Za-z0-9_])JSR\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*[,)]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    if (!names.Contains(match.Groups[1].Value)) result[Key(Name(program), Name(routine) + "/JSR/" + match.Groups[1].Value)] = (XmlProjects.PathOf(routine), "Direct literal JSR target is absent: " + match.Groups[1].Value);
            }
            return result;
        }
        var inherited = RoutineReferences(oldPrograms, maskedBefore);
        foreach (var (key, value) in RoutineReferences(programs, maskedAfter)) Finding(inherited.ContainsKey(key) ? "inherited" : "blocked", "direct-routine-reference", value.Location, value.Detail);

        var removedTags = oldTags.Where(t => !newTags.ContainsKey(t.Key)).ToArray(); if (removedTags.Length == 0) return;
        var removedNames = removedTags.Select(t => Name(t.Value)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var beforeLocals = oldPrograms.ToDictionary(p => Name(p)!, p => p.Elements("Tags").Elements("Tag").Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var afterLocals = programs.ToDictionary(p => Name(p)!, p => p.Elements("Tags").Elements("Tag").Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var removedKeys = removedTags.Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase); var tokens = 0;
        void References(string code, string? program, string location, bool alias)
        {
            var diagnostics = new List<string>(); var masked = new string(TagReferences.MaskSource(code, diagnostics.Add));
            if (code.Length > 1024 * 1024) throw new OperationException("structural_limit", "A reference source exceeds 1 MiB.");
            var symbols = Regex.Matches(masked, @"(?<![A-Za-z0-9_])(?:JSR|JMP|LBL|EVENT)\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(m => (Start: m.Groups[1].Index, Length: m.Groups[1].Length)).ToArray();
            foreach (var token in TagReferences.Lex(code, diagnostics.Add))
            {
                if (++tokens > 100000) throw new OperationException("structural_limit", "More than 100000 reference tokens.");
                if (!removedNames.Contains(token.Name) || (!alias && symbols.Any(s => token.Start == s.Start && token.Name.Length == s.Length))) continue;
                var local = program is not null && (afterLocals.GetValueOrDefault(program)?.Contains(token.Name) == true || beforeLocals.GetValueOrDefault(program)?.Contains(token.Name) == true);
                var binding = token.Program ?? (local ? program : null);
                if (removedKeys.Contains(Key(binding, token.Name))) Finding("blocked", "removed-tag-reference", location, "Lexical operand/alias still addresses a removed tag or would capture another scope: " + code.Substring(token.Start, token.Length) + ".");
            }
            if (diagnostics.Count > 0 && removedNames.Any(n => Regex.IsMatch(masked, @"(?<![A-Za-z0-9_])" + Regex.Escape(n) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))) Finding("blocked", "reference-unsupported", location, "Unresolved source syntax touches a removed tag; review the exact binding.");
        }
        foreach (var program in programs)
        foreach (var routine in program.Elements("Routines").Elements("Routine"))
        {
            var type = (string?)routine.Attribute("Type");
            if (type is "RLL" or "ST") References(Code(routine), Name(program), XmlProjects.PathOf(routine), false);
            else if (routine.DescendantsAndSelf().SelectMany(e => e.Attributes().Select(a => a.Value)).Any(v => removedNames.Any(n => Regex.IsMatch(v, @"(?<![A-Za-z0-9_])" + Regex.Escape(n) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))))
                Finding("blocked", "reference-unsupported", XmlProjects.PathOf(routine), "Visible unsupported-routine attribute mentions a removed tag.");
        }
        foreach (var tag in newTags.Values.Where(t => t.Attribute("AliasFor") is not null)) References((string)tag.Attribute("AliasFor")!, ProgramName(tag), XmlProjects.PathOf(tag) + "/@AliasFor", true);
    }
}
