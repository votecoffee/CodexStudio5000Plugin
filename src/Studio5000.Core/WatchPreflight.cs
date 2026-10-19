using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record WatchPreflightIssue(string Code, string Message);
public sealed record WatchPreflightRequest(WatchListRequest? Settings = null, string? WatchId = null, BrowserWatchRequest? Browser = null);
public sealed record WatchPreflightCheck(string Name, string Status, WatchPreflightIssue? Issue = null);
public sealed record WatchSelectionPreflight(WatchTag Tag, string Status, string? DataType, int[] Dimensions,
    int? ScalarReads, WatchPreflightIssue[] Issues);
public sealed record WatchReadBudget(int SupportedSelections, int UniqueSupportedLeafReads, int SupportedPlanNodes,
    int ScalarValueBytes, double NominalReadsPerSecond, bool Complete, bool FitsListLimits);
public sealed record WatchPreflightReport(string Backend, string Mode, string? WatchId, int? WatchRevision,
    string SourceSha256, bool SourceHashMatches, string Status, WatchPreflightCheck[] Checks,
    WatchSelectionPreflight[] Selections, WatchReadBudget? Budget, string Coverage);

public sealed partial class ProjectService
{
    public Task<WatchPreflightReport> PreflightWatchAsync(WatchPreflightRequest request, CancellationToken ct = default)
    {
        if ((request.Settings is null ? 0 : 1) + (request.WatchId is null ? 0 : 1) + (request.Browser is null ? 0 : 1) != 1)
            throw new OperationException("watch_preflight_source", "Choose exactly one settings draft, saved watchId, or browser selection.");
        if (request.WatchId is not null) return PreflightSavedWatchAsync(request.WatchId, ct);
        if (request.Settings is not null) return PreflightWatchAsync(request.Settings, ct);
        var browser = request.Browser!; var project = Get(browser.ProjectId); Expect(project, browser.ExpectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select a saved Logix snapshot.");
        var source = RevisionFile(project.Id, project.Revision);
        var settings = new WatchListRequest(browser.Name, browser.Tags, browser.ExpectedSha256, SourcePath: source,
            ControllerTargetId: browser.ControllerTargetId, Mode: browser.Mode, IntervalMs: browser.IntervalMs, HistoryLimit: browser.HistoryLimit, Backend: browser.Backend);
        // This private, hash-bound revision path comes only from an authorized project lookup; no export artifact is created.
        return PreflightWatchCoreAsync(settings, ct, null, null, source);
    }

    public Task<WatchPreflightReport> PreflightSavedWatchAsync(string id, CancellationToken ct = default)
    {
        WatchDefinition definition;
        lock (watchLock) definition = Clone(Watch(id).Definition)!;
        return PreflightWatchCoreAsync(definition.Settings, ct, definition.Id, definition.Revision);
    }

    public Task<WatchPreflightReport> PreflightWatchAsync(WatchListRequest request, CancellationToken ct = default)
        => PreflightWatchCoreAsync(request, ct, null, null);

    /// <summary>Read-only observations of local files/configuration. Never opens a reader, SDK project, job or connection.</summary>
    private async Task<WatchPreflightReport> PreflightWatchCoreAsync(WatchListRequest request, CancellationToken ct, string? watchId, int? watchRevision, string? projectSource = null)
    {
        WatchValidation.ValidateSettings(request);
        var checks = new List<WatchPreflightCheck>(); var rows = new List<WatchSelectionPreflight>();
        void Block(string name, string code, string message) => checks.Add(new(name, "blocked", new(code, message)));
        ControllerTarget? target = null; var targetKnown = true;
        try { target = WatchTarget(request); checks.Add(new("target", "passed")); }
        catch (OperationException ex) { targetKnown = false; Block("target", ex.Code, ex.Message); }
        if (request.Mode == "Online" && (!options.EnableControllerOperations || target is null))
            Block("controller_access", "sdk_controller_disabled", "Online polling requires enabled controller access and an explicit target. This report does not authorize access.");
        else checks.Add(new("controller_access", "passed"));
        var workerExists = request.Backend == "Sdk" ? native.Available : File.Exists(options.CipBridgePath);
        if (workerExists) checks.Add(new("reader_configuration", "passed"));
        else Block("reader_configuration", request.Backend == "Sdk" ? "native_unavailable" : "cip_unavailable", "The selected reader executable is not configured or is absent. No executable was launched.");
        if (request.Backend == "LibPlcTag")
        {
            if (target?.CipRead is null) Block("cip_target", "cip_target", "The target needs an explicit CIP gateway and route.");
            else
            {
                try { CipWatchPlanning.ValidateTarget(target.CipRead); checks.Add(new("cip_target", "passed")); }
                catch (OperationException ex) { Block("cip_target", ex.Code, ex.Message); }
            }
        }

        // Path authorization errors remain request errors, rather than turning arbitrary paths into diagnostic probes.
        var source = projectSource ?? WatchSource(request); var extension = Path.GetExtension(source).ToLowerInvariant();
        string hash; XDocument? metadata = null;
        await using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        {
            if (file.Length > 500L * 1024 * 1024) throw new OperationException("file_too_large", "Watch source exceeds 500 MiB.");
            if (extension == ".l5x" && file.Length <= 64L * 1024 * 1024)
            {
                var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, ct); hash = Hash(bytes);
                try { metadata = XmlProjects.Parse(bytes); checks.Add(new("metadata", "passed")); }
                catch (Exception ex) when (ex is OperationException or XmlException)
                { Block("metadata", ex is OperationException operation ? operation.Code : "invalid_xml", "Saved L5X metadata is invalid: " + ex.Message); }
            }
            else
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
                if (request.Backend == "LibPlcTag" && extension != ".l5x") Block("metadata", "cip_source", "CIP reads require an L5X declaration snapshot; ACD metadata is not inspected here.");
                else checks.Add(new("metadata", "deferred", new("watch_metadata_deferred", "ACD and L5X files larger than 64 MiB are hashed only. Export/register a bounded L5X snapshot to inspect declarations; no native conversion was attempted.")));
            }
        }
        var hashMatches = hash.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        if (hashMatches) checks.Add(new("source_hash", "passed"));
        else Block("source_hash", "stale_revision", "The source differs from the watch's expected SHA-256. Rows describe the observed source hash, not an approved replacement.");

        Dictionary<string, StructureType>? types = null;
        if (metadata is not null)
        {
            try
            {
                var declarations = LogixStructures.Types(metadata);
                if (declarations.Any(t => string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 256 || t.Members.Any(m =>
                    string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 256 || string.IsNullOrWhiteSpace(m.DataType) || m.DataType.Length > 256)))
                    throw new OperationException("watch_declaration_invalid", "UDT/member declarations need nonempty names and type names up to 256 characters.");
                types = declarations.ToDictionary(t => t.Name, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is OperationException or ArgumentException or InvalidOperationException)
            {
                checks.RemoveAll(c => c.Name == "metadata"); Block("metadata", "watch_declaration_invalid", "Saved UDT declarations cannot be inspected: " + ex.Message);
                metadata = null;
            }
        }
        var leaves = new Dictionary<string, CipReadNode>(StringComparer.Ordinal); var nodes = 0; var supported = 0;
        foreach (var tag in request.Tags)
        {
            ct.ThrowIfCancellationRequested();
            var issues = new List<WatchPreflightIssue>(); string? type = null; int[] dimensions = []; int? reads = null; var deferred = metadata is null;
            try { _ = WatchValidation.TagPath(tag); WatchValidation.MemberPath(tag.MemberPath); }
            catch (OperationException ex) { issues.Add(new(ex.Code, ex.Message)); }
            if (issues.Count == 0 && targetKnown)
            {
                try { ValidateWatchPolicy(request with { Tags = [tag] }, target); }
                catch (OperationException ex) { issues.Add(new(ex.Code, ex.Message)); }
            }
            if (issues.Count == 0 && metadata is not null)
            {
                try
                {
                    var tagType = (string?)LogixStructures.Tag(metadata, tag.TagName, tag.ProgramName).Attribute("DataType");
                    if (string.IsNullOrWhiteSpace(tagType) || tagType.Length > 256)
                        throw new OperationException("watch_declaration_invalid", "The selected tag needs a nonempty declared type name up to 256 characters.");
                    if (request.Backend == "LibPlcTag")
                    {
                        var plan = CipWatchPlanning.Build(metadata, hash, [tag], types!);
                        var value = plan.Selections[0].Value; type = value.DataType; dimensions = value.Dimensions; reads = plan.UniqueLeafReads;
                        AddNodes(value); supported++;
                    }
                    else (type, dimensions) = ResolveWatchDeclaration(metadata, types!, tag);
                }
                catch (OperationException ex)
                {
                    if (ex.Code == "watch_declaration_unavailable") deferred = true;
                    issues.Add(new(ex.Code, ex.Message));
                }
            }
            rows.Add(new(tag, issues.Any(i => i.Code != "watch_declaration_unavailable") ? "blocked" : deferred ? "deferred" : "supported", type, dimensions, reads, issues.ToArray()));
        }
        WatchReadBudget? budget = null;
        if (request.Backend == "LibPlcTag")
        {
            var complete = rows.All(r => r.Status == "supported");
            var fits = leaves.Count <= 256 && nodes <= 2048;
            budget = new(supported, leaves.Count, nodes, leaves.Values.Sum(n => CipScalarBytes(n.DataType)),
                Math.Round(leaves.Count * 1000d / request.IntervalMs, 3), complete, fits);
            if (!fits) Block("read_budget", "cip_plan_limit", "Combined supported selections exceed 256 unique scalar reads or 2048 plan nodes. Reduce the list; no selections were dropped.");
            else checks.Add(new("read_budget", complete ? "passed" : "deferred", complete ? null : new("cip_partial_budget", "Counts cover supported selections only; unsupported/deferred rows are excluded and still prevent a complete preflight.")));
        }
        var status = checks.Any(c => c.Status == "blocked") || rows.Any(r => r.Status == "blocked") ? "blocked"
            : checks.Any(c => c.Status == "deferred") || rows.Any(r => r.Status == "deferred") ? "incomplete" : "local_checks_passed";
        return new(request.Backend, request.Mode, watchId, watchRevision, hash, hashMatches, status, checks.ToArray(), rows.ToArray(), budget,
            "Local source declarations/configuration only; no reader, SDK/native verification or controller connection. Executable existence does not establish loadability. Supported rows do not establish live availability/type identity or performance. CIP counts exclude blocked/deferred rows; scalar bytes exclude protocol/JSON overhead. Nominal reads per second assumes the requested interval is achievable; samples are sequential, not atomic. Saving and starting repeat their own checks; this report grants no authorization and reserves no session capacity.");

        void AddNodes(CipReadNode node)
        {
            nodes++;
            if (node.Kind == "scalar") leaves.TryAdd(node.Address!, node);
            foreach (var child in node.Children) AddNodes(child);
        }
    }

    private static int CipScalarBytes(string type) => type switch
    { "BOOL" or "SINT" or "USINT" => 1, "INT" or "UINT" => 2, "DINT" or "UDINT" or "REAL" => 4, "LINT" or "ULINT" or "LREAL" => 8, _ => 0 };

    private static (string Type, int[] Dimensions) ResolveWatchDeclaration(XDocument metadata, IReadOnlyDictionary<string, StructureType> types, WatchTag tag)
    {
        var declaration = LogixStructures.Tag(metadata, tag.TagName, tag.ProgramName);
        if ((string?)declaration.Attribute("TagType") != "Base" || declaration.Attribute("AliasFor") is not null)
            throw new OperationException("watch_alias", "Select an exact base tag; aliases are not followed.");
        var type = (string?)declaration.Attribute("DataType") ?? "Unknown";
        var dimensions = LogixStructures.Dimensions((string?)declaration.Attribute("Dimensions"));
        foreach (Match part in Regex.Matches(tag.MemberPath ?? "", @"([A-Za-z_][A-Za-z0-9_]*)|\[(\d+)\]", RegexOptions.CultureInvariant))
        {
            if (part.Groups[2].Success)
            {
                if (dimensions.Length == 0 || !int.TryParse(part.Groups[2].Value, out var index) || index >= dimensions[0])
                    throw new OperationException("watch_member", "Array index is outside the saved declaration.");
                dimensions = dimensions[1..];
            }
            else
            {
                if (dimensions.Length > 0) throw new OperationException("watch_member", "Index the declared array before selecting a member.");
                if (!types.TryGetValue(type, out var structure)) throw new OperationException("watch_declaration_unavailable", "No exported member declaration for " + type + "; native verification is deferred.");
                var member = structure.Members.SingleOrDefault(m => m.Name == part.Value)
                    ?? throw new OperationException("watch_member", "Member is absent from the saved declaration.");
                if (member.Hidden) throw new OperationException("watch_member", "Hidden implementation members cannot be selected.");
                type = member.DataType; dimensions = member.Dimensions;
            }
        }
        return (type, dimensions);
    }
}
