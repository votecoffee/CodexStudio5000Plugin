using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private (ProjectInfo Project, byte[] Bytes) StructuralSource(string id, string sha256, int revision)
    { var source = PreservationSource(new(id, sha256)); if (source.Project.Revision != revision) throw new OperationException("stale_revision", "Structural review requires the exact source revision and hash."); return source; }
    public async Task<StructuralContext> StructuralContextAsync(StructuralContextRequest request, CancellationToken ct = default)
    { using (await EnterWorkspaceAsync(request.ProjectId, ct)) { var source = StructuralSource(request.ProjectId, request.ExpectedSha256, request.ExpectedRevision); return StructuralEditing.Context(source.Project, source.Bytes, request); } }
    private string StructuralPlanHash(SdkInputFile? plan)
    {
        if (plan is null || !StructuralEditing.Hash(plan.Sha256) || (plan.Path is null) == (plan.ArtifactId is null)) throw new OperationException("structural_plan", "Bind one actual reviewed plan path or artifact ID and its exact SHA-256.");
        var path = plan.Path is not null ? AllowedSource(plan.Path) : Download(plan.ArtifactId!).Path;
        if (Path.GetExtension(path).ToLowerInvariant() is not (".json" or ".md" or ".txt") || new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new OperationException("structural_plan", "Use a nonempty JSON/Markdown/text review plan up to 1 MiB; its content is never executed.");
        var hash = Hash(File.ReadAllBytes(path)); if (!hash.Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "The reviewed plan file/artifact changed; review and pin it again."); return hash;
    }
    private (StructuralEditPreview Preview, byte[] Bytes) BuildStructuralEdits(StructuralEditRequest request)
    { var source = StructuralSource(request.ProjectId, request.ExpectedSha256, request.ExpectedRevision); return StructuralEditing.Build(source.Project, source.Bytes, request, StructuralPlanHash(request.Plan), StructuralReferences(request.Scope?.ReferenceAdditions)); }
    private ResolvedStructuralReference[] StructuralReferences(StructuralReferenceAddition[]? additions)
    {
        additions ??= [];
        if (additions.Length > 200 || additions.Any(a => a is null || string.IsNullOrWhiteSpace(a.Key) || a.Key.Length > 128 || a.Kind is not ("aoi" or "udt") || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 256 || !StructuralEditing.Hash(a.ExpectedElementSha256) || string.IsNullOrWhiteSpace(a.XPath) || a.XPath.Length > 8192 || a.Source is null || !StructuralEditing.Hash(a.Source.Sha256) || (a.Source.Path is null) == (a.Source.ArtifactId is null)) || additions.Select(a => a.Key).Distinct(StringComparer.Ordinal).Count() != additions.Length)
            throw new OperationException("structural_reference", "Use up to 200 uniquely keyed AOI/UDT reference additions with exact source and element hashes.");
        var documents = new Dictionary<string, (XDocument Doc, XmlSourceDocument Source)>(StringComparer.OrdinalIgnoreCase); var results = new List<ResolvedStructuralReference>(); long total = 0, referenceBytes = 0;
        foreach (var addition in additions)
        {
            var path = addition.Source.Path is not null ? AllowedSource(addition.Source.Path) : Download(addition.Source.ArtifactId!).Path;
            if (Path.GetExtension(path).ToLowerInvariant() != ".l5x" || new FileInfo(path).Length > 64 * 1024 * 1024) throw new OperationException("structural_reference", "Reference components require an exact saved L5X up to 64 MiB.");
            if (!documents.TryGetValue(addition.Source.Sha256, out var parsed))
            {
                var bytes = File.ReadAllBytes(path); if ((referenceBytes += bytes.Length) > 128 * 1024 * 1024) throw new OperationException("structural_limit", "Distinct reference L5X inputs exceed 128 MiB total."); if (!Hash(bytes).Equals(addition.Source.Sha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Reference L5X bytes differ from the pinned source hash.");
                var doc = ModuleConnectionEditing.Parse(bytes); parsed = (doc, new(bytes, doc)); documents.Add(addition.Source.Sha256, parsed);
            }
            else if (!Hash(File.ReadAllBytes(path)).Equals(addition.Source.Sha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "A declared reference path no longer matches the pinned file hash.");
            var node = XmlProjects.Select(parsed.Doc, addition.XPath);
            var supported = addition.Kind == "udt" ? node.Name == "DataType" && node.Parent?.Name == "DataTypes" : node.Parent?.Name == "AddOnInstructionDefinitions" && (node.Name == "AddOnInstructionDefinition" || node.Name == "EncodedData" && (string?)node.Attribute("EncodedType") == "AddOnInstructionDefinition");
            if (!supported || (string?)node.Attribute("Name") != addition.Name || !parsed.Source.Fingerprint(node).Equals(addition.ExpectedElementSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("structural_reference", "The exact reference node kind/name/hash does not match its declared addition.");
            var xml = parsed.Source.Lexical(node); if (xml.Length > 1024 * 1024 || (total += xml.Length) > 4 * 1024 * 1024) throw new OperationException("structural_limit", "Reference additions support 1 MiB per component and 4 MiB total text.");
            var types = parsed.Doc.Root!.Element("Controller")!.Elements("DataTypes").Elements("DataType").ToDictionary(t => (string)t.Attribute("Name")!, StringComparer.OrdinalIgnoreCase);
            var layouts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var pending = new Queue<string>(node.DescendantsAndSelf().Attributes("DataType").Select(a => a.Value));
            while (pending.TryDequeue(out var name)) if (types.TryGetValue(name, out var type) && layouts.TryAdd(name, StructuralEditing.LayoutFingerprint(type))) foreach (var member in type.Elements("Members").Elements("Member").Attributes("DataType")) pending.Enqueue(member.Value);
            var aoiNames = parsed.Doc.Root.Element("Controller")!.Elements("AddOnInstructionDefinitions").Elements().Select(a => (string?)a.Attribute("Name")).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usedAois = node.DescendantsAndSelf().Attributes("DataType").Select(a => a.Value).Where(aoiNames.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var code in node.Descendants().Where(e => e.Name.LocalName is "Text" or "Line"))
            {
                var masked = new string(TagReferences.MaskSource(code.Value, _ => { }));
                foreach (Match match in Regex.Matches(masked, @"(?<![A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) if (aoiNames.Contains(match.Groups[1].Value)) usedAois.Add(match.Groups[1].Value);
            }
            usedAois.Remove(addition.Name); results.Add(new(new(addition.Key, addition.Kind, addition.Name, addition.Source.Sha256.ToLowerInvariant(), addition.ExpectedElementSha256.ToLowerInvariant()), xml, layouts, usedAois.ToArray()));
        }
        return results.ToArray();
    }
    private Artifact StructuralArtifact(string projectId, string format, byte[] bytes)
    { var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project." + format), bytes); var artifact = new Artifact(id, projectId, format, Hash(bytes), bytes.Length, StructuralEditing.Boundary); Save(Path.Combine(dir, "artifact.json"), artifact); return artifact; }
    public async Task<StructuralEditPreview> PreviewStructuralEditsAsync(StructuralEditRequest request, CancellationToken ct = default)
    {
        using (await EnterWorkspaceAsync(request.ProjectId, ct))
        {
            var preview = BuildStructuralEdits(request).Preview;
            var receipt = StructuralArtifact(request.ProjectId, "json", JsonSerializer.SerializeToUtf8Bytes(new StructuralPreviewDocument("structural_preview", DateTimeOffset.UtcNow, preview), Json));
            Audit("structural_preview", Get(request.ProjectId), preview.Source.Sha256, preview.ResultSha256, receipt.Id);
            return preview with { PreviewReceipt = receipt };
        }
    }
    private (Artifact Artifact, StructuralEditPreview Preview) StructuralReceipt(string id, string sha256)
    {
        if (!StructuralEditing.Hash(sha256)) throw new OperationException("structural_receipt", "Supply the exact retained preview receipt hash.");
        var stored = Download(id); var artifact = stored.Artifact; if (artifact.Format != "json" || artifact.Bytes > 8 * 1024 * 1024 || !artifact.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("structural_receipt", "Retained preview receipt hash/format does not match.");
        var document = JsonSerializer.Deserialize<StructuralPreviewDocument>(File.ReadAllBytes(stored.Path), Json);
        if (document?.Kind != "structural_preview" || document.Preview is null || document.Preview.Source.Id != artifact.ProjectId) throw new OperationException("structural_receipt", "Select a retained structural preview receipt.");
        return (artifact, document.Preview);
    }
    public async Task<StructuralEditStatus> StructuralEditStatusAsync(string previewReceiptId, string expectedSha256, CancellationToken ct = default)
    {
        var retained = StructuralReceipt(previewReceiptId, expectedSha256);
        using (await EnterWorkspaceAsync(retained.Preview.Source.Id, ct))
        {
            var path = Path.Combine(ArtifactDir(previewReceiptId), "structural-apply.json"); var status = File.Exists(path) ? Read<StructuralApplyState>(path) : null;
            if (status is not null && status.ReviewSha256 != retained.Preview.ReviewSha256 || status?.Result is { } stored && stored.ReviewSha256 != retained.Preview.ReviewSha256) throw new OperationException("state_tampered", "Structural apply state does not match its retained review.");
            if (status?.Result is { } result) { _ = Download(result.Candidate.Id); _ = Download(result.Receipt.Id); }
            return new(retained.Artifact, retained.Preview with { PreviewReceipt = retained.Artifact }, status?.Status ?? "previewed", status?.Result, StructuralEditing.Boundary);
        }
    }
    public async Task<StructuralEditResult> ApplyStructuralEditsAsync(StructuralApplyRequest request, CancellationToken ct = default)
    {
        if (request.Preview is null) throw new OperationException("structural_policy", "Supply the complete reviewed preview request.");
        var retained = StructuralReceipt(request.PreviewReceiptId, request.PreviewReceiptSha256);
        using (await EnterWorkspaceAsync(retained.Preview.Source.Id, ct))
        {
            if (!retained.Preview.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase) || Hash(JsonSerializer.SerializeToUtf8Bytes(retained.Preview.Request, Json)) != Hash(JsonSerializer.SerializeToUtf8Bytes(request.Preview, Json))) throw new OperationException("stale_revision", "Apply must match the exact retained preview request and review hash.");
            var path = Path.Combine(ArtifactDir(request.PreviewReceiptId), "structural-apply.json");
            if (File.Exists(path))
            {
                var previous = Read<StructuralApplyState>(path);
                if (previous.ReviewSha256 != retained.Preview.ReviewSha256 || previous.Result is { } stored && stored.ReviewSha256 != retained.Preview.ReviewSha256) throw new OperationException("state_tampered", "Structural apply state does not match its retained review.");
                if (previous.Status == "applied" && previous.Result is { } result) { _ = Download(result.Candidate.Id); _ = Download(result.Receipt.Id); return result; }
                throw new OperationException("structural_apply_indeterminate", "A previous apply did not record completion. Inspect retained status/artifacts; it is not replayed automatically.");
            }
            var built = BuildStructuralEdits(request.Preview);
            if (built.Preview.ReviewSha256 != retained.Preview.ReviewSha256) throw new OperationException("stale_revision", "The structural sources, plan or findings changed; preview again.");
            if (!built.Preview.CanApply) throw new OperationException("structural_blocked", "Resolve all blocked scope, preservation, dependency and safety findings before apply.");
            Save(path, new StructuralApplyState("applying-indeterminate", built.Preview.ReviewSha256, null));
            var project = PublishDiagnosticProject(built.Bytes, "REVIEW ONLY: Scoped structural batch", "structural-edit:" + built.Preview.Source.Id);
            var candidate = StructuralArtifact(project.Id, "l5x", built.Bytes);
            var receipt = StructuralArtifact(project.Id, "json", JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "structural_apply", toolVersion = WorkspaceIdentity.Version, createdAt = DateTimeOffset.UtcNow, previewReceipt = retained.Artifact, preview = built.Preview, project, candidate, nativeAcceptance = "not-performed" }, Json));
            var applied = new StructuralEditResult(project, candidate, receipt, built.Preview.ReviewSha256); Save(path, new StructuralApplyState("applied", built.Preview.ReviewSha256, applied));
            Audit("structural_apply", project, built.Preview.Source.Sha256, candidate.Sha256, receipt.Id); return applied;
        }
    }
}
