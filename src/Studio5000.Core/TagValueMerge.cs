using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record TagValueMap(string SourceTag, string? SourceProgram, string DestinationTag, string? DestinationProgram);
public sealed record TagValueMergeRequest(string SourceProjectId, string SourceSha256, string DestinationProjectId, string DestinationSha256,
    TagValueMap[]? Tags = null, string? SourceProgram = null, string? DestinationProgram = null);
public sealed record TagValueMergeRow(TagValueMap Tag, string? DataType, string Status, string? Before, string? After, string? Message);
public sealed record TagValueMergeReport(ProjectInfo Source, ProjectInfo Destination, string Origin, DateTimeOffset? CapturedAt,
    string ReviewSha256, string ResultSha256, bool CanApply, int Changed, TagValueMergeRow[] Rows, string Coverage);
public sealed record TagValueMergeApplyRequest(TagValueMergeRequest Preview, string ExpectedReviewSha256);

// Copies one authoritative exported representation, never a mixture of stale raw/L5K/decorated values.
public static class TagValueMerging
{
    private static readonly Dictionary<string, (int Bits, bool Signed)> Integers = new()
    { ["SINT"] = (8, true), ["INT"] = (16, true), ["DINT"] = (32, true), ["LINT"] = (64, true),
      ["USINT"] = (8, false), ["UINT"] = (16, false), ["UDINT"] = (32, false), ["ULINT"] = (64, false) };
    internal static XElement[] Tags(XDocument doc, string? program)
    {
        if (program is not null) WatchValidation.Identifier(program);
        var controller = doc.Root!.Element("Controller")!;
        var owner = program is null ? controller : controller.Elements("Programs").Elements("Program").SingleOrDefault(p => (string?)p.Attribute("Name") == program)
            ?? throw new OperationException("merge_scope", "The exact program was not found.");
        return owner.Elements("Tags").Elements("Tag").ToArray();
    }
    private static string Schema(XDocument doc, XElement tag)
    {
        if ((string?)tag.Attribute("TagType") != "Base" || tag.AncestorsAndSelf().Any(e => (string?)e.Attribute("Class") == "Safety"))
            throw new OperationException("merge_unsupported", "Select standard base tags; aliases, produced/consumed and safety tags are excluded.");
        var types = LogixStructures.Types(doc).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var graph = new SortedDictionary<string, object>(StringComparer.Ordinal); var active = new HashSet<string>(); var budget = 8192;
        void Visit(string type, int depth)
        {
            if (--budget < 0 || depth > 16) throw new OperationException("merge_limit", "UDT declaration exceeds the merge budget.");
            if (Integers.ContainsKey(type) || type is "BOOL" or "BIT" or "REAL" or "LREAL" or "STRING") return;
            if (active.Contains(type)) throw new OperationException("merge_unsupported", "Recursive declarations cannot be merged.");
            if (graph.ContainsKey(type)) return;
            if (!types.TryGetValue(type, out var declaration)) throw new OperationException("merge_unsupported", "An exported UDT declaration is required for " + type + ".");
            var xml = doc.Descendants("DataType").Single(t => (string?)t.Attribute("Name") == type);
            if ((string?)xml.Attribute("Class") == "Safety") throw new OperationException("merge_unsupported", "Safety data types are excluded.");
            graph.Add(type, declaration.Members.Select(m => new { m.Name, m.DataType, m.Dimensions, m.Hidden, m.Target, m.BitNumber }).ToArray());
            active.Add(type); foreach (var member in declaration.Members) Visit(member.DataType, depth + 1); active.Remove(type);
        }
        var type = (string?)tag.Attribute("DataType") ?? "Unknown"; Visit(type, 0);
        return JsonSerializer.Serialize(new { type, dimensions = LogixStructures.Dimensions((string?)tag.Attribute("Dimensions")), graph }, ProjectService.Json);
    }
    private static XElement Value(XDocument doc, XElement tag)
    {
        var formats = tag.Elements("Data").Where(d => (string?)d.Attribute("Format") is "Decorated" or "String").ToArray();
        if (formats.Length != 1) throw new OperationException("merge_value", "Exactly one Decorated or String representation is required; raw/L5K-only values are unsupported.");
        var data = formats[0]; var extracted = LogixStructures.ExtractTag(tag, null);
        if (extracted.Quality != "snapshot") throw new OperationException("merge_value", extracted.Error ?? "Value could not be decoded.");
        var type = (string)tag.Attribute("DataType")!; var dims = LogixStructures.Dimensions((string?)tag.Attribute("Dimensions"));
        var types = LogixStructures.Types(doc).ToDictionary(t => t.Name, StringComparer.Ordinal); var budget = 10000;
        if ((string?)data.Attribute("Format") == "String")
        {
            if (type != "STRING" || dims.Length != 0 || extracted.Value is null || extracted.Value.GetValue<string>().Length > 82 ||
                (int?)data.Attribute("Length") != extracted.Value.GetValue<string>().Length)
                throw new OperationException("merge_value", "String data requires a scalar STRING with an exact length of at most 82 characters.");
        }
        else Check(data.Elements().Single(), type, dims, 0);
        return new XElement(data);

        void Check(XElement element, string expected, int[] dimensions, int depth)
        {
            if (--budget < 0 || depth > 32) throw new OperationException("merge_limit", "Value exceeds 10000 nodes or 32 levels.");
            if ((string?)element.Attribute("DataType") != expected) throw new OperationException("merge_value", "Value type differs from its declaration.");
            if (dimensions.Length > 0)
            {
                if (element.Name.LocalName is not ("Array" or "ArrayMember") || !LogixStructures.Dimensions((string?)element.Attribute("Dimensions")).SequenceEqual(dimensions) || dimensions.Length > 3)
                    throw new OperationException("merge_value", "Value array shape differs from its declaration.");
                var count = dimensions.Aggregate(1L, (a, b) => checked(a * b));
                if (count > budget || element.Elements().LongCount() != count) throw new OperationException("merge_value", "Array value is incomplete or too large.");
                var seen = new HashSet<string>();
                foreach (var child in element.Elements())
                {
                    var index = (string?)child.Attribute("Index") ?? "";
                    if (child.Name != "Element" || !seen.Add(index) || index.Length < 3 || index[0] != '[' || index[^1] != ']') throw new OperationException("merge_value", "Invalid/duplicate array index.");
                    var indices = index[1..^1].Split(',');
                    if (indices.Length != dimensions.Length || indices.Where((v, i) => !int.TryParse(v, out var n) || n < 0 || n >= dimensions[i]).Any())
                        throw new OperationException("merge_value", "Array index is outside its declared dimensions.");
                    if (index != "[" + string.Join(',', indices.Select(v => int.Parse(v, CultureInfo.InvariantCulture))) + "]") throw new OperationException("merge_value", "Array indices must use canonical coordinates.");
                    if (child.HasElements) Check(child.Elements().Single(), expected, [], depth + 1);
                    else { --budget; Scalar(expected, (string?)child.Attribute("Value") ?? throw new OperationException("merge_value", "Missing scalar value.")); }
                }
            }
            else if (element.Name.LocalName is "Structure" or "StructureMember")
            {
                if (!types.TryGetValue(expected, out var declaration)) throw new OperationException("merge_unsupported", "A structure needs exported member declarations.");
                var members = declaration.Members.Where(m => !m.Hidden).ToArray(); var children = element.Elements().ToArray();
                if (children.Length != members.Length) throw new OperationException("merge_value", "Structure member count differs from its declaration.");
                for (var i = 0; i < members.Length; i++)
                {
                    if ((string?)children[i].Attribute("Name") != members[i].Name) throw new OperationException("merge_value", "Structure members differ from their declaration/order.");
                    Check(children[i], members[i].DataType, members[i].Dimensions, depth + 1);
                }
            }
            else
            {
                if (element.Name.LocalName is not ("DataValue" or "DataValueMember") || element.HasElements) throw new OperationException("merge_value", "Unsupported value encoding.");
                Scalar(expected, (string?)element.Attribute("Value") ?? throw new OperationException("merge_value", "Missing scalar value."));
            }
        }
    }
    private static void Scalar(string type, string value)
    {
        if (type is "BOOL" or "BIT") { if (value is not ("0" or "1" or "true" or "false" or "TRUE" or "FALSE")) throw new OperationException("merge_value", "Invalid boolean."); return; }
        if (type is "REAL" or "LREAL")
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || type == "REAL" && !float.IsFinite((float)number))
                throw new OperationException("merge_value", "Invalid or nonfinite floating point value.");
            return;
        }
        if (!Integers.TryGetValue(type, out var integer)) throw new OperationException("merge_unsupported", "Unsupported scalar type " + type + ".");
        var plain = value.Replace("_", ""); BigInteger numberValue; var radix = plain.Split('#');
        if (radix.Length == 2)
        {
            if (!int.TryParse(radix[0], out var basis) || basis is not (2 or 8 or 16) || radix[1].Length is 0 or > 64) throw new OperationException("merge_value", "Invalid integer radix.");
            numberValue = 0;
            foreach (var c in radix[1]) { var digit = "0123456789ABCDEF".IndexOf(char.ToUpperInvariant(c)); if (digit < 0 || digit >= basis) throw new OperationException("merge_value", "Invalid radix digit."); numberValue = numberValue * basis + digit; }
            if (numberValue >= BigInteger.One << integer.Bits) throw new OperationException("merge_value", "Integer bit pattern is out of range.");
        }
        else if (!BigInteger.TryParse(plain, NumberStyles.Integer, CultureInfo.InvariantCulture, out numberValue) ||
            numberValue < (integer.Signed ? -(BigInteger.One << (integer.Bits - 1)) : 0) || numberValue >= BigInteger.One << (integer.Signed ? integer.Bits - 1 : integer.Bits))
            throw new OperationException("merge_value", "Integer value is out of range.");
    }
    internal static (TagValueMergeRow Row, XElement? Data) Inspect(XDocument source, XDocument destination, TagValueMap map)
    {
        string? type = null;
        try
        {
            var from = LogixStructures.Tag(source, map.SourceTag, map.SourceProgram); var to = LogixStructures.Tag(destination, map.DestinationTag, map.DestinationProgram);
            type = (string?)from.Attribute("DataType");
            if (Schema(source, from) != Schema(destination, to)) throw new OperationException("merge_schema", "Data type, array dimensions or reachable UDT layout differs.");
            var data = Value(source, from);
            string Summary(XElement tag) { var v = LogixStructures.ExtractTag(tag, null); var text = v.Value?.ToJsonString() ?? v.Error ?? "unavailable"; return text.Length > 1024 ? text[..1024] + "…" : text; }
            var prior = to.Elements("Data").SingleOrDefault(d => (string?)d.Attribute("Format") == (string?)data.Attribute("Format"));
            var equal = prior is not null && XNode.DeepEquals(prior, data) && to.Elements("Data").Count() == 1;
            return (new(map, type, equal ? "unchanged" : "ready", Summary(to), Summary(from), null), data);
        }
        catch (Exception ex) when (ex is OperationException or FormatException or OverflowException or InvalidOperationException or ArgumentException)
        { return (new(map, type, ex is OperationException op ? op.Code : "merge_value", null, null, ex.Message[..Math.Min(512, ex.Message.Length)]), null); }
    }
    internal static byte[] Serialize(XDocument document)
    {
        XmlProjects.Validate(document); using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None, OmitXmlDeclaration = document.Declaration is null })) document.Save(writer);
        return stream.ToArray();
    }
}

public sealed partial class ProjectService
{
    public Task<SdkJob> CaptureTagValueSourceAsync(StructurePullRequest request, CancellationToken ct = default) => PullStructuresAsync(request, ct);

    private (TagValueMergeReport Report, byte[] Bytes) BuildTagValueMerge(TagValueMergeRequest request)
    {
        var (source, sourceDoc, destination, targetDoc) = ValueMergeDocuments(request.SourceProjectId, request.SourceSha256, request.DestinationProjectId, request.DestinationSha256);
        var maps = request.Tags ?? TagValueMerging.Tags(sourceDoc, request.SourceProgram).Select(t => new TagValueMap((string)t.Attribute("Name")!, request.SourceProgram, (string)t.Attribute("Name")!, request.DestinationProgram)).ToArray();
        if (maps.Length is 0 or > 256 || maps.Any(m => m is null)) throw new OperationException("merge_limit", "Select between 1 and 256 whole base tags per merge.");
        foreach (var map in maps)
        { WatchValidation.Identifier(map.SourceTag); WatchValidation.Identifier(map.DestinationTag); if (map.SourceProgram is not null) WatchValidation.Identifier(map.SourceProgram); if (map.DestinationProgram is not null) WatchValidation.Identifier(map.DestinationProgram); }
        if (maps.Select(m => (m.DestinationProgram, m.DestinationTag)).Distinct().Count() != maps.Length) throw new OperationException("merge_duplicate", "Each destination tag may be selected once.");
        var rows = new List<TagValueMergeRow>();
        foreach (var map in maps)
        {
            var item = TagValueMerging.Inspect(sourceDoc, targetDoc, map); rows.Add(item.Row);
            if (item.Row.Status != "ready") continue;
            var to = LogixStructures.Tag(targetDoc, map.DestinationTag, map.DestinationProgram);
            var prior = to.Elements("Data").FirstOrDefault();
            if (prior is null) to.Add(item.Data); else { prior.AddBeforeSelf(item.Data); to.Elements("Data").Where(d => d != item.Data).Remove(); }
        }
        var bytes = TagValueMerging.Serialize(targetDoc); if (bytes.Length > 64 * 1024 * 1024) throw new OperationException("merge_limit", "Merged project exceeds 64 MiB.");
        var origin = "saved_project"; DateTimeOffset? capturedAt = null;
        if (source.SourcePath.StartsWith("sdk-job:", StringComparison.Ordinal))
        {
            var job = SdkJob(source.SourcePath[8..]);
            if (source.Revision == 0 && job.Status == "succeeded" && job.Methods.SequenceEqual(new[] { "UploadToNewProjectAsync" }) && job.ResultProject?.CurrentSha256 == source.CurrentSha256)
            { origin = "controller_upload_snapshot"; capturedAt = source.CreatedAt; }
        }
        var resultHash = Hash(bytes); var review = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, source.Revision, destinationRevision = destination.Revision, rows, resultHash }, Json));
        var report = new TagValueMergeReport(source, destination, origin, capturedAt, review, resultHash,
            rows.Any(r => r.Status == "ready") && rows.All(r => r.Status is "ready" or "unchanged"), rows.Count(r => r.Status == "ready"), rows.ToArray(),
            "Whole standard base-tag values only; exact type/dimensions/UDT layout required. Copies validated Decorated or String data, removes competing destination Data representations; preserves definitions, logic, forces and source files. Uploads are point-in-time, non-atomic snapshots. Apply changes only the managed offline destination. Export a separate L5X/ACD; native import/build and runtime validation are separate.");
        if (JsonSerializer.SerializeToUtf8Bytes(report, Json).Length > 3 * 1024 * 1024) throw new OperationException("merge_limit", "Preview exceeds 3 MiB; select fewer tags.");
        return (report, bytes);
    }
    public async Task<TagValueMergeReport> PreviewTagValueMergeAsync(TagValueMergeRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return BuildTagValueMerge(request).Report; } finally { gate.Release(); } }
    public async Task<ProjectInfo> ApplyTagValueMergeAsync(TagValueMergeApplyRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var (report, bytes) = BuildTagValueMerge(request.Preview);
            if (!report.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Merge differs from its reviewed preview.");
            if (!report.CanApply) throw new OperationException("merge_blocked", "Select compatible changed tags and preview again.");
            ct.ThrowIfCancellationRequested(); var before = report.Destination;
            var next = before with { Revision = before.Revision + 1, CurrentSha256 = report.ResultSha256 };
            AtomicBytes(RevisionFile(next.Id, next.Revision), bytes);
            Audit("value_merge_intent", before, before.CurrentSha256, next.CurrentSha256, report.ReviewSha256);
            Save(ProjectMeta(next.Id), next);
            Audit("value_merge", next, before.CurrentSha256, next.CurrentSha256, report.ReviewSha256 + ":source=" + report.Source.Id + ":sha256=" + report.Source.CurrentSha256);
            return next;
        }
        finally { gate.Release(); }
    }
}
