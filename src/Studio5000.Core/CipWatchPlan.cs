using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record CipReadTarget(string Gateway, string Route, int TimeoutMs = 3000);
public sealed record CipWatchPlanRequest(string ExpectedSha256, WatchTag[] Tags);
public sealed record CipReadNode(string Name, string Kind, string DataType, int[] Dimensions, string? Address,
    CipReadNode[] Children, int? CipType = null);
public sealed record CipWatchSelection(WatchTag Tag, CipReadNode Value);
public sealed record CipWatchPlanReport(string SourceSha256, CipWatchSelection[] Selections, int UniqueLeafReads, string Coverage);

/// <summary>Symbolic leaf reads use exported declarations; no UDT byte offsets or controller discovery are inferred.</summary>
public static class CipWatchPlanning
{
    private static readonly Dictionary<string, int> AtomicTypes = new(StringComparer.Ordinal)
    { ["BOOL"] = 0xc1, ["BIT"] = 0xc1, ["SINT"] = 0xc2, ["INT"] = 0xc3, ["DINT"] = 0xc4, ["LINT"] = 0xc5,
      ["USINT"] = 0xc6, ["UINT"] = 0xc7, ["UDINT"] = 0xc8, ["ULINT"] = 0xc9, ["REAL"] = 0xca, ["LREAL"] = 0xcb };
    public static void ValidateTarget(CipReadTarget target)
    {
        if (!IPAddress.TryParse(target.Gateway, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.ToString() != target.Gateway || address.GetAddressBytes()[0] is 0 or >= 224 ||
            string.IsNullOrEmpty(target.Route) || target.Route.Length > 63 || !Regex.IsMatch(target.Route, @"^[0-9]+(?:,[0-9]+)+$", RegexOptions.CultureInvariant) ||
            target.Route.Split(',') is not { Length: >= 2 and <= 16 } parts || parts.Length % 2 != 0 || parts.Any(p => !byte.TryParse(p, out _)) ||
            target.TimeoutMs is < 100 or > 30000)
            throw new OperationException("cip_target", "CIP reads require a canonical unicast IPv4 gateway, 2..16 comma-separated route bytes in port/link pairs, and timeout 100..30000 ms.");
    }
    public static CipWatchPlanReport Build(XDocument document, string sha256, WatchTag[] tags)
    {
        if (tags is null || tags.Length is < 1 or > 64 || tags.Any(t => t is null || string.IsNullOrWhiteSpace(t.Key) || t.Key.Length > 100) ||
            tags.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != tags.Length)
            throw new OperationException("cip_plan", "Provide 1..64 tags with distinct keys up to 100 characters.");
        var types = LogixStructures.Types(document).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal); var nodes = 0;
        CipReadNode Build(string name, string type, int[] dimensions, string symbol, int[] indices, HashSet<string> active, int depth)
        {
            if (++nodes > 2048 || depth > 16 || symbol.Length > 2048)
                throw new OperationException("cip_plan_limit", "Read plan exceeds 2048 nodes, 16 levels or 2048 address characters; select a smaller member/array slice.");
            if (dimensions.Length > 0)
            {
                if (dimensions[0] > 256) throw new OperationException("cip_plan_limit", "An array slice exceeds 256 elements; select an element/member.");
                return new(name, "array", type, dimensions, null, Enumerable.Range(0, dimensions[0])
                    .Select(i => Build(i.ToString(System.Globalization.CultureInfo.InvariantCulture), type, dimensions[1..], symbol, [..indices, i], active, depth + 1)).ToArray());
            }
            if (indices.Length > 0) symbol += "[" + string.Join(',', indices) + "]";
            if (AtomicTypes.TryGetValue(type, out var code))
            {
                var normalized = type == "BIT" ? "BOOL" : type;
                if (leaves.TryGetValue(symbol, out var previous) && previous != normalized) throw new OperationException("cip_plan", "A symbolic address has conflicting declared types.");
                leaves[symbol] = normalized;
                if (leaves.Count > 256) throw new OperationException("cip_plan_limit", "A list exceeds 256 unique scalar leaf reads; select smaller members.");
                return new(name, "scalar", normalized, [], symbol, [], code);
            }
            if (type == "STRING" || !types.TryGetValue(type, out var declaration)) throw new OperationException("cip_type_unsupported", "No supported scalar or exported UDT declaration for " + type + ". Use the SDK backend for strings and unavailable built-in/module templates.");
            if (!active.Add(type)) throw new OperationException("cip_plan", "Recursive UDT declarations cannot be expanded.");
            var members = new List<CipReadNode>();
            foreach (var member in declaration.Members.Where(m => !m.Hidden))
            {
                WatchValidation.Identifier(member.Name);
                if (member.ExternalAccess == "None") throw new OperationException("cip_external_access", "Selected structure contains a member with ExternalAccess=None; select readable members.");
                members.Add(Build(member.Name, member.DataType, member.Dimensions, symbol + "." + member.Name, [], active, depth + 1));
            }
            active.Remove(type);
            if (members.Count == 0) throw new OperationException("cip_type_unsupported", "The selected UDT has no visible declared members.");
            return new(name, "object", type, [], null, members.ToArray());
        }
        var selections = new List<CipWatchSelection>();
        foreach (var tag in tags)
        {
            _ = WatchValidation.TagPath(tag); WatchValidation.MemberPath(tag.MemberPath);
            var declaration = LogixStructures.Tag(document, tag.TagName, tag.ProgramName);
            if ((string?)declaration.Attribute("TagType") != "Base" || declaration.Attribute("AliasFor") is not null)
                throw new OperationException("watch_alias", "Select an exact base tag; aliases are not followed.");
            if ((string?)declaration.Attribute("ExternalAccess") == "None") throw new OperationException("cip_external_access", "Tag ExternalAccess=None does not permit live reads.");
            var type = (string?)declaration.Attribute("DataType") ?? "Unknown";
            var dimensions = LogixStructures.Dimensions((string?)declaration.Attribute("Dimensions"));
            var symbol = (tag.ProgramName is null ? "" : "Program:" + tag.ProgramName + ".") + tag.TagName; var indices = new List<int>();
            foreach (Match part in Regex.Matches(tag.MemberPath ?? "", @"([A-Za-z_][A-Za-z0-9_]*)|\[(\d+)\]", RegexOptions.CultureInvariant))
            {
                if (part.Groups[2].Success)
                {
                    if (dimensions.Length == 0 || !int.TryParse(part.Groups[2].Value, out var index) || index >= dimensions[0]) throw new OperationException("cip_member", "Array index is outside declared dimensions.");
                    indices.Add(index); dimensions = dimensions[1..];
                    if (dimensions.Length == 0) { symbol += "[" + string.Join(',', indices) + "]"; indices.Clear(); }
                }
                else
                {
                    if (dimensions.Length > 0 || !types.TryGetValue(type, out var structure)) throw new OperationException("cip_member", "No exported member declaration at this path.");
                    var member = structure.Members.SingleOrDefault(m => m.Name == part.Value)
                        ?? throw new OperationException("cip_member", "Member is absent from the saved declaration.");
                    if (member.Hidden || member.ExternalAccess == "None") throw new OperationException("cip_external_access", "Hidden or ExternalAccess=None members cannot be read.");
                    symbol += "." + member.Name; type = member.DataType; dimensions = member.Dimensions;
                }
            }
            selections.Add(new(tag, Build(tag.Key, type, dimensions, symbol, indices.ToArray(), [], 0)));
        }
        return new(sha256, selections.ToArray(), leaves.Count,
            "Saved-declaration symbolic leaf plan only: BOOL, signed/unsigned integers, REAL/LREAL, bounded arrays and visible exported UDT members. No packed UDT offsets, strings, template inference or controller discovery. Live reads must verify each returned CIP scalar type. Leaf reads are sequential and not an atomic structure snapshot; missing/changed live members or types fail. Planning never connects.");
    }

    public static async Task<WatchValue[]> ReadAsync(CipWatchPlanReport plan, Func<CipReadNode, CancellationToken, Task<JsonNode?>> read, CancellationToken ct)
    {
        var cache = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        async Task<JsonNode?> Value(CipReadNode node)
        {
            ct.ThrowIfCancellationRequested();
            if (node.Kind == "scalar")
            {
                if (!cache.TryGetValue(node.Address!, out var value)) { value = await read(node, ct); cache.Add(node.Address!, value); }
                return value?.DeepClone();
            }
            if (node.Kind == "array") { var array = new JsonArray(); foreach (var child in node.Children) array.Add(await Value(child)); return array; }
            var result = new JsonObject(); foreach (var child in node.Children) result.Add(child.Name, await Value(child)); return result;
        }
        var values = new List<WatchValue>();
        foreach (var selection in plan.Selections) values.Add(new(selection.Tag.Key, selection.Tag.TagName, selection.Tag.ProgramName,
            selection.Value.DataType, await Value(selection.Value), "good"));
        return values.ToArray();
    }

    public static void ValidateType(CipReadNode node, byte[] rawType)
    {
        if (rawType.Length != 2 || rawType[0] != node.CipType || rawType[1] != 0)
            throw new OperationException("cip_type_mismatch", "Live CIP type differs from the saved declaration for " + node.Address + ". Refresh the L5X snapshot before restarting.");
    }

    public static WatchValue[] ValidateValues(WatchTag[] tags, WatchValue[]? values)
    {
        if (values is null || values.Length != tags.Length || values.Any(v => v is null) || values.Select(v => v.Key).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new OperationException("cip_reply", "Reader returned missing or duplicate selections.");
        return tags.Select(tag => values.SingleOrDefault(v => v.Key == tag.Key && v.TagName == tag.TagName && v.ProgramName == tag.ProgramName && v.Quality == "good")
            ?? throw new OperationException("cip_reply", "Reader returned an unauthorized or unsuccessful selection.")).ToArray();
    }
}

public sealed partial class ProjectService
{
    public CipWatchPlanReport PlanCipWatch(string id, CipWatchPlanRequest request)
    {
        var project = Get(id); Expect(project, request.ExpectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select a saved Logix L5X snapshot.");
        return CipWatchPlanning.Build(XmlProjects.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision))), project.CurrentSha256, request.Tags);
    }
}
