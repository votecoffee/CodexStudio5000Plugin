using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record ModuleConnectionBrowseRequest(string ProjectId, string ExpectedSha256, string? Query = null, int Offset = 0, int Limit = 100);
public sealed record ModuleConnectionInfo(string ModuleName, string ConnectionName, string XPath, string? CatalogNumber,
    Dictionary<string, string> Timing, string[] SafetyEvidence);
public sealed record ModuleConnectionBrowse(ComparisonSnapshot Source, int Total, int Matching, int Offset, int Limit,
    int UnsupportedConnectionElements, ModuleConnectionInfo[] Connections, string Boundary);
public sealed record ModuleConnectionEdit(string ModuleName, string ConnectionName, string Attribute, string ExpectedValue, string Value);
public sealed record ModuleConnectionEditRequest(string ProjectId, string ExpectedSha256, ModuleConnectionEdit[] Edits, string SafetyHandling = "block");
public sealed record ModuleConnectionChange(ModuleConnectionEdit Edit, string XPath, bool Changed, ModuleConnectionInfo Before, ModuleConnectionInfo After);
public sealed record ModuleConnectionPlan(ModuleConnectionEditRequest Request, ComparisonSnapshot Source, bool CanApply,
    string? ResultSha256, string ReviewSha256, string XmlPreservation, ModuleConnectionChange[] Changes, string[] Findings, string Boundary);
public sealed record ModuleConnectionApplyRequest(ModuleConnectionEditRequest Preview, string ExpectedReviewSha256);
public sealed record ModuleConnectionResult(ProjectInfo Project, Artifact Receipt, string ReviewSha256, bool ReviewOnly = true, string NativeAcceptance = "not-performed");

internal static class ModuleConnectionEditing
{
    internal const string Boundary = "Offline review copy only. Exact existing Module/Communications/Connections/Connection attributes RPI, NetworkDelayMultiplier and ReactionTimeLimit; source/hash/before-value and review guards. Raw scalar spans preserve encoding/BOM, other attributes, connection payloads/forces, safety signatures and all unselected bytes. Explicit safety metadata requires safetyHandling=review-only; absent markers do not prove a nonsafety module. No timing recalculation, module-profile validation, safety reaction-time assessment, native import/readback, SDK job or controller connection. Native AOP recalculation and readback acceptance are separate.";
    private static readonly string[] Editable = ["RPI", "NetworkDelayMultiplier", "ReactionTimeLimit"];
    private static readonly string[] Context = ["Type", "RPI", "TimeoutMultiplier", "NetworkDelayMultiplier", "ReactionTimeLimit"];
    internal static XDocument Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo); XmlProjects.Validate(document);
        _ = new XmlSourceDocument(bytes, document); return document;
    }
    internal static IEnumerable<(XElement Module, XElement Connection)> Connections(XDocument doc)
    {
        foreach (var module in doc.Root!.Element("Controller")!.Elements("Modules").Elements("Module"))
        {
            var connections = module.Elements("Communications").Elements("Connections").Elements("Connection").ToArray();
            if (connections.GroupBy(c => (string?)c.Attribute("Name"), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new OperationException("module_connection_ambiguous", "Duplicate connection names prevent an exact review.");
            foreach (var connection in connections) yield return (module, connection);
        }
    }
    private static string[] Safety(XElement module, XElement connection)
    {
        var evidence = new List<string>();
        if (((string?)connection.Attribute("Type"))?.Contains("Safety", StringComparison.OrdinalIgnoreCase) == true) evidence.Add("Connection/@Type=" + (string?)connection.Attribute("Type"));
        foreach (var name in new[] { "TimeoutMultiplier", "NetworkDelayMultiplier", "ReactionTimeLimit" }) if (connection.Attribute(name) is not null) evidence.Add("Connection/@" + name + " present");
        if (module.Attribute("SafetyEnabled") is { } enabled && !enabled.Value.Equals("false", StringComparison.OrdinalIgnoreCase) && enabled.Value != "0") evidence.Add("Module/@SafetyEnabled=" + enabled.Value);
        if (module.Attribute("SafetyNetwork") is not null || module.Descendants().Any(e => e.Name.LocalName.StartsWith("Safety", StringComparison.Ordinal))) evidence.Add("Module safety network/configuration metadata present");
        if (LogixPreservation.Safety(module)) evidence.Add("Safety class on module/ancestor");
        return evidence.ToArray();
    }
    private static ModuleConnectionInfo Info(XElement module, XElement connection) => new((string?)module.Attribute("Name") ?? "", (string?)connection.Attribute("Name") ?? "",
        XmlProjects.PathOf(connection), (string?)module.Attribute("CatalogNumber"), Context.Where(n => connection.Attribute(n) is not null).ToDictionary(n => n, n => (string)connection.Attribute(n)!), Safety(module, connection));
    internal static ModuleConnectionBrowse Browse(ProjectInfo project, byte[] bytes, ModuleConnectionBrowseRequest request)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 256 || request.Query?.Length > 256) throw new OperationException("module_connection_page", "Use offset >= 0, limit 1..256 and a query up to 256 characters.");
        var document = Parse(bytes); var all = Connections(document).Take(10001).ToArray();
        if (all.Length > 10000) throw new OperationException("module_connection_limit", "At most 10000 module connections can be reviewed.");
        var matches = all.Select(c => Info(c.Module, c.Connection)).Where(c => string.IsNullOrEmpty(request.Query) || (c.ModuleName + " " + c.ConnectionName + " " + c.CatalogNumber).Contains(request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var unsupported = document.Root!.Element("Controller")!.Elements("Modules").Elements("Module").Elements("Communications").Elements("Connections").Elements().Count(e => e.Name != "Connection");
        var report = new ModuleConnectionBrowse(new(project.Id, project.Name, project.Revision, project.CurrentSha256), all.Length, matches.Length, request.Offset, request.Limit, unsupported, matches.Skip(request.Offset).Take(request.Limit).ToArray(), Boundary);
        if (JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json).Length > 4 * 1024 * 1024) throw new OperationException("module_connection_limit", "Module connection page exceeds 4 MiB; use a smaller limit.");
        return report;
    }
    internal static (ModuleConnectionPlan Plan, byte[] Bytes) Build(ProjectInfo project, byte[] bytes, ModuleConnectionEditRequest request)
    {
        if (request.Edits is null || request.Edits.Length is < 1 or > 200 || request.SafetyHandling is not ("block" or "review-only")) throw new OperationException("module_connection_policy", "Use 1..200 exact edits and safetyHandling block or review-only.");
        foreach (var edit in request.Edits)
        {
            if (edit is null || string.IsNullOrWhiteSpace(edit.ModuleName) || edit.ModuleName.Length > 256 || string.IsNullOrWhiteSpace(edit.ConnectionName) || edit.ConnectionName.Length > 256 ||
                !Editable.Contains(edit.Attribute) || edit.ExpectedValue is null || edit.ExpectedValue.Length > 128 || !ValidValue(edit.Attribute, edit.Value))
                throw new OperationException("module_connection_policy", "Select existing RPI (positive UInt32), NetworkDelayMultiplier (integer 10..600) or ReactionTimeLimit (decimal 0..5500032); supply unsigned invariant decimal strings in native exported units. Native/module-specific restrictions remain unchecked.");
        }
        if (request.Edits.Select(e => (e.ModuleName, e.ConnectionName, e.Attribute)).Distinct().Count() != request.Edits.Length) throw new OperationException("module_connection_policy", "Each exact connection attribute can be selected once per review.");
        var doc = Parse(bytes); var source = new XmlSourceDocument(bytes, doc); var connections = Connections(doc).Take(10001).ToArray();
        if (connections.Length > 10000) throw new OperationException("module_connection_limit", "At most 10000 module connections can be reviewed.");
        var patches = new List<XmlSourceDocument.Patch>(); var changes = new List<ModuleConnectionChange>(); var findings = new List<string>();
        var selected = new List<(ModuleConnectionEdit Edit, XElement Module, XElement Connection, ModuleConnectionInfo Before)>();
        // Capture all before values before changing any related attribute.
        foreach (var edit in request.Edits)
        {
            var found = connections.Where(c => (string?)c.Module.Attribute("Name") == edit.ModuleName && (string?)c.Connection.Attribute("Name") == edit.ConnectionName).ToArray();
            if (found.Length != 1) throw new OperationException("module_connection_target", "Select exactly one existing module/connection name pair from the source inventory.");
            var (module, connection) = found[0]; var attribute = connection.Attribute(edit.Attribute) ?? throw new OperationException("module_connection_target", "The selected attribute must already exist on the exact connection.");
            if (attribute.Value != edit.ExpectedValue) throw new OperationException("stale_revision", "Connection before-value differs; browse and preview the current source again.");
            selected.Add((edit, module, connection, Info(module, connection)));
            if (attribute.Value != edit.Value)
            {
                var span = source.AttributeValue(attribute); patches.Add(new(span.Start, span.End, edit.Value));
            }
        }
        foreach (var (edit, _, connection, _) in selected) connection.SetAttributeValue(edit.Attribute, edit.Value);
        foreach (var (edit, module, connection, before) in selected)
            changes.Add(new(edit, before.XPath + "/@" + edit.Attribute, edit.ExpectedValue != edit.Value, before, Info(module, connection)));
        var safetyChange = changes.Any(c => c.Changed && c.Before.SafetyEvidence.Length > 0);
        if (safetyChange) findings.Add(request.SafetyHandling == "review-only" ? "Safety-related timing changes explicitly selected for a REVIEW ONLY copy; signatures are retained, not revalidated." : "BLOCKED: Safety-related timing changes require explicit safetyHandling=review-only.");
        findings.Add("Related timing fields are not recalculated. Review native module-profile restrictions, safety task/output timing and actual imported/exported values separately.");
        var result = source.Apply(patches); var readback = XmlProjects.Parse(result); var after = Connections(readback).ToDictionary(c => ((string)c.Module.Attribute("Name")!, (string)c.Connection.Attribute("Name")!));
        foreach (var edit in request.Edits) if ((string?)after[(edit.ModuleName, edit.ConnectionName)].Connection.Attribute(edit.Attribute) != edit.Value) throw new OperationException("module_connection_preservation", "Patched scalar readback differs from the exact reviewed edit.");
        var resultHash = ProjectService.Hash(result); var canApply = !safetyChange || request.SafetyHandling == "review-only";
        var review = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, sourceSha256 = project.CurrentSha256, resultHash, changes, findings, toolVersion = WorkspaceIdentity.Version }, ProjectService.Json));
        var plan = new ModuleConnectionPlan(request, new(project.Id, project.Name, project.Revision, project.CurrentSha256), canApply, canApply ? resultHash : null, review, XmlScalarEdits.Preservation, changes.ToArray(), findings.ToArray(), Boundary);
        if (JsonSerializer.SerializeToUtf8Bytes(plan, ProjectService.Json).Length > 4 * 1024 * 1024) throw new OperationException("module_connection_limit", "Module connection review exceeds 4 MiB.");
        return (plan, result);
    }
    private static bool ValidValue(string attribute, string? text)
    {
        if (text is null || text.Length is < 1 or > 32 || !System.Text.RegularExpressions.Regex.IsMatch(text, @"\A[0-9]+(?:\.[0-9]+)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
        if (attribute == "ReactionTimeLimit") return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 5500032;
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer) && (attribute == "RPI" ? integer > 0 : integer is >= 10 and <= 600);
    }
}

public sealed partial class ProjectService
{
    public async Task<ModuleConnectionBrowse> BrowseModuleConnectionsAsync(ModuleConnectionBrowseRequest request, CancellationToken ct = default)
    {
        using (await EnterWorkspaceAsync(request.ProjectId, ct)) { var input = PreservationSource(new(request.ProjectId, request.ExpectedSha256)); return ModuleConnectionEditing.Browse(input.Project, input.Bytes, request); }
    }
    private (ModuleConnectionPlan Plan, byte[] Bytes) BuildModuleConnections(ModuleConnectionEditRequest request)
    { var input = PreservationSource(new(request.ProjectId, request.ExpectedSha256)); return ModuleConnectionEditing.Build(input.Project, input.Bytes, request); }
    public async Task<ModuleConnectionPlan> PreviewModuleConnectionsAsync(ModuleConnectionEditRequest request, CancellationToken ct = default)
    { using (await EnterWorkspaceAsync(request.ProjectId, ct)) return BuildModuleConnections(request).Plan; }
    public async Task<ModuleConnectionResult> ApplyModuleConnectionsAsync(ModuleConnectionApplyRequest request, CancellationToken ct = default)
    {
        using (await EnterWorkspaceAsync(request.Preview.ProjectId, ct))
        {
            var built = BuildModuleConnections(request.Preview);
            if (!built.Plan.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Module connection review changed; preview the exact source and edits again.");
            if (!built.Plan.CanApply) throw new OperationException("module_connection_blocked", "Review safety handling before publishing a connection review copy.");
            var project = PublishDiagnosticProject(built.Bytes, "REVIEW ONLY: Module connection timing", "module-connections:" + built.Plan.Source.Id);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "module_connection_review", toolVersion = WorkspaceIdentity.Version, createdAt = DateTimeOffset.UtcNow, plan = built.Plan, result = project, nativeAcceptance = "not-performed" }, Json);
            var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project.json"), bytes);
            var receipt = new Artifact(id, project.Id, "json", Hash(bytes), bytes.Length, ModuleConnectionEditing.Boundary); Save(Path.Combine(dir, "artifact.json"), receipt);
            return new(project, receipt, built.Plan.ReviewSha256);
        }
    }
}
