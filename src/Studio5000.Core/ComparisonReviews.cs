using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record ComparisonReviewExportRequest(ProjectCompareRequest Comparison, string Format = "html", bool IncludeXml = false, bool IncludeLadder = false);
public sealed record ComparisonSnapshot(string Id, string Name, int Revision, string Sha256);
public sealed record ComparisonReviewDrawing(string Status, string[] Diagnostics, string? Svg);
public sealed record ComparisonReviewChange(ProjectDifference Difference, ComparisonReviewDrawing? LeftDrawing, ComparisonReviewDrawing? RightDrawing);
public sealed record ComparisonReviewDocument(int SchemaVersion, string ApplicationVersion, DateTimeOffset CreatedAt,
    ComparisonSnapshot Left, ComparisonSnapshot Right, string? Query, bool IncludeRungNumbers, bool IncludeXml, bool IncludeLadder,
    int TotalChanges, int Added, int Removed, int Modified, ComparisonReviewChange[] Changes, string Boundary);
public sealed record ComparisonReviewEntry(string Id, DateTimeOffset CreatedAt, string Format, string ArtifactId, string Sha256, long Bytes,
    ComparisonSnapshot Left, ComparisonSnapshot Right, string? Query, bool IncludeRungNumbers, bool IncludeXml, bool IncludeLadder, int TotalChanges, int IncludedChanges,
    string? CatalogSha256 = null);
public sealed record ComparisonReviewPage(int Total, int Offset, int Limit, ComparisonReviewEntry[] Reviews);
public sealed record ComparisonReviewDeleteRequest(string ExpectedSha256);
public sealed record ComparisonReviewExport(Artifact Artifact, ComparisonReviewEntry Review);

public static class ComparisonReviews
{
    public const int MaximumChanges = 5000, MaximumDrawings = 200, MaximumBytes = 16 * 1024 * 1024, MaximumHistory = 200;
    public const string Boundary = "Structural saved-XML comparison only; no native semantic equivalence, live power flow, simulation or runtime validation. " +
        "All matching changes are included, independent of GUI pagination. XML snippets, when requested, retain explicit truncation flags. " +
        "Passive drawings cover changed Rung entries only; whole added/removed routines are not expanded. Source file paths, controller routes and credentials are omitted. Component names, hashes and requested content may be sensitive.";

    internal static byte[] Bytes(ComparisonReviewDocument document, string format)
    {
        var bytes = format == "json" ? JsonSerializer.SerializeToUtf8Bytes(document, ProjectService.Json) : Encoding.UTF8.GetBytes(Html(document));
        if (bytes.Length > MaximumBytes) throw new OperationException("comparison_review_limit", "Review exceeds 16 MiB; narrow the filter or omit XML/drawings.");
        return bytes;
    }
    private static string Html(ComparisonReviewDocument d)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "(absent)");
        var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        b.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>Logix comparison review</title>");
        b.Append("<style>body{font:15px system-ui,sans-serif;margin:2rem;color:#172b42;background:#f5f7fa}main{max-width:1400px;margin:auto}article,header{background:white;border:1px solid #ccd5df;border-radius:6px;padding:1rem;margin:1rem 0}code,pre{font:13px ui-monospace,monospace;overflow-wrap:anywhere}pre{white-space:pre-wrap;background:#f0f3f7;padding:1rem}table{border-collapse:collapse;width:100%}th,td{padding:.6rem;border:1px solid #ccd5df;text-align:left;vertical-align:top}.sides{display:grid;grid-template-columns:1fr 1fr;gap:1rem}.drawing{overflow:auto;background:white}.drawing svg{max-width:100%;height:auto}h2{font-size:1.1rem}.muted{color:#485e76}@media(max-width:800px){.sides{grid-template-columns:1fr}}@media print{body{margin:0;background:white}article{break-inside:avoid}details{display:block}}</style></head><body><main><header><h1>Logix comparison review</h1>");
        b.Append("<p>").Append(H(d.CreatedAt.ToString("O"))).Append(" · RockwellMCPServer ").Append(H(d.ApplicationVersion)).Append("</p><table><tr><th>Snapshot</th><th>Project</th><th>Revision / SHA-256</th></tr>");
        foreach (var (label, snapshot) in new[] { ("Left", d.Left), ("Right", d.Right) })
            b.Append("<tr><th>").Append(label).Append("</th><td>").Append(H(snapshot.Name)).Append("<br><code>").Append(H(snapshot.Id)).Append("</code></td><td>r").Append(snapshot.Revision).Append("<br><code>").Append(H(snapshot.Sha256)).Append("</code></td></tr>");
        b.Append("</table><p>").Append(d.TotalChanges).Append(" total changes: +").Append(d.Added).Append(" / −").Append(d.Removed).Append(" / ~").Append(d.Modified)
            .Append(". Included matches: ").Append(d.Changes.Length).Append(". Filter: <code>").Append(H(d.Query ?? "(all)")).Append("</code>.</p><p>Rung numbers: ").Append(d.IncludeRungNumbers)
            .Append(" · XML: ").Append(d.IncludeXml).Append(" · Ladder drawings: ").Append(d.IncludeLadder).Append("</p><p class=\"muted\">").Append(H(d.Boundary)).Append("</p></header>");
        var checkedLength = b.Length; var byteCount = Encoding.UTF8.GetByteCount(b.ToString());
        for (var i = 0; i < d.Changes.Length; i++)
        {
            var row = d.Changes[i]; var c = row.Difference;
            b.Append("<article><h2>").Append(i + 1).Append(". ").Append(H(c.Change)).Append(" · ").Append(H(c.Kind)).Append("</h2><p><code>").Append(H(c.Path)).Append("</code></p><div class=\"sides\">");
            void Side(string label, string? xpath, string? hash, string? xml, bool truncated, ComparisonReviewDrawing? drawing)
            {
                b.Append("<section><h3>").Append(label).Append("</h3><p><code>").Append(H(xpath)).Append("</code></p><p>Component SHA-256: <code>").Append(H(hash)).Append("</code></p>");
                if (drawing is not null)
                {
                    b.Append("<p>Drawing: ").Append(H(drawing.Status)).Append("</p>");
                    // SVG is generated exclusively by LadderPreview; all source labels are encoded there.
                    if (drawing.Svg is not null) b.Append("<div class=\"drawing\">").Append(drawing.Svg).Append("</div>");
                    foreach (var diagnostic in drawing.Diagnostics) b.Append("<p class=\"muted\">").Append(H(diagnostic)).Append("</p>");
                }
                if (d.IncludeXml) b.Append("<details><summary>Normalized XML").Append(truncated ? " (truncated)" : "").Append("</summary><pre>").Append(H(xml)).Append("</pre></details>");
                b.Append("</section>");
            }
            Side("Left", c.LeftXPath, c.LeftSha256, c.LeftXml, c.LeftTruncated, row.LeftDrawing);
            Side("Right", c.RightXPath, c.RightSha256, c.RightXml, c.RightTruncated, row.RightDrawing);
            b.Append("</div></article>");
            byteCount += Encoding.UTF8.GetByteCount(b.ToString(checkedLength, b.Length - checkedLength)); checkedLength = b.Length;
            if (byteCount > MaximumBytes) throw new OperationException("comparison_review_limit", "Review exceeds 16 MiB; narrow the filter or omit XML/drawings.");
        }
        return b.Append("</main></body></html>").ToString();
    }
}

public sealed partial class ProjectService
{
    private string ComparisonReviewDirectory() => ReviewDirectory("comparison-reviews");
    private ComparisonReviewEntry ReadComparisonReview(string id) => Read<ComparisonReviewEntry>(ExistingReviewPath("comparison-reviews", id));
    public async Task<ComparisonReviewExport> ExportComparisonReviewAsync(ComparisonReviewExportRequest request, CancellationToken ct = default)
    {
        if (request.Comparison is null || request.Format is not ("html" or "json")) throw new OperationException("invalid_comparison_review", "Select pinned snapshots and format html or json.");
        await gate.WaitAsync(ct);
        try
        {
            var directory = ComparisonReviewDirectory();
            if (Directory.Exists(directory) && Directory.GetFiles(directory, "*.json").Length >= ComparisonReviews.MaximumHistory)
                throw new OperationException("comparison_review_limit", "Review history has 200 entries; forget an entry before exporting another report.");
            var comparison = request.Comparison;
            var left = Get(comparison.LeftProjectId); var right = Get(comparison.RightProjectId);
            if (left.Kind != "logix" || right.Kind != "logix") throw new OperationException("not_logix", "Select two registered Logix snapshots.");
            Expect(left, comparison.LeftSha256); Expect(right, comparison.RightSha256);
            var a = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(left.Id, left.Revision))); var b = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(right.Id, right.Revision)));
            var report = ProjectComparison.CompareForReview(left, a, right, b, comparison);
            if (request.IncludeLadder && report.Differences.Count(c => c.Kind == "Rung") > ComparisonReviews.MaximumDrawings)
                throw new OperationException("comparison_review_limit", "Ladder drawings support up to 200 changed rung entries; narrow the filter or omit drawings.");
            ComparisonReviewDrawing? Draw(ProjectInfo info, XDocument doc, string? xpath)
            {
                if (xpath is null) return null;
                var rung = XmlProjects.Select(doc, xpath); var routine = rung.Parent?.Parent;
                if (rung.Name != "Rung" || routine?.Name != "Routine" || (string?)routine.Attribute("Type") != "RLL")
                    return new("unavailable", ["Changed element is not a rung in an RLL routine."], null);
                var preview = LadderPreview.Build(info, routine, new(info.CurrentSha256, XmlProjects.PathOf(routine), rung.ElementsBeforeSelf("Rung").Count(), 1)).Rungs.Single();
                return new(preview.Status, preview.Diagnostics, preview.Diagram?.Svg);
            }
            var rows = report.Differences.Select(c => new ComparisonReviewChange(request.IncludeXml ? c : c with { LeftXml = null, RightXml = null },
                request.IncludeLadder && c.Kind == "Rung" ? Draw(left, a, c.LeftXPath) : null, request.IncludeLadder && c.Kind == "Rung" ? Draw(right, b, c.RightXPath) : null)).ToArray();
            static ComparisonSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.Name, p.Revision, p.CurrentSha256);
            var document = new ComparisonReviewDocument(1, WorkspaceIdentity.Version, DateTimeOffset.UtcNow, Snapshot(left), Snapshot(right), comparison.Query,
                comparison.IncludeRungNumbers, request.IncludeXml, request.IncludeLadder, report.TotalChanges, report.Added, report.Removed, report.Modified, rows, report.Boundary + " " + ComparisonReviews.Boundary);
            var bytes = ComparisonReviews.Bytes(document, request.Format); ct.ThrowIfCancellationRequested();
            var catalog = ComparisonAnnotations.Catalog(Hash(bytes), document.Changes);
            var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(catalog, Json);
            if (catalogBytes.Length > ComparisonReviews.MaximumBytes) throw new OperationException("comparison_review_limit", "Change catalog exceeds 16 MiB; narrow the comparison filter.");
            var id = Guid.NewGuid().ToString("N"); var artifactDir = ArtifactDir(id); Directory.CreateDirectory(artifactDir);
            AtomicBytes(Path.Combine(artifactDir, "project." + request.Format), bytes);
            AtomicBytes(Path.Combine(artifactDir, "changes.json"), catalogBytes);
            var artifact = new Artifact(id, id, request.Format, Hash(bytes), bytes.Length, ComparisonReviews.Boundary);
            Save(Path.Combine(artifactDir, "artifact.json"), artifact);
            var entry = new ComparisonReviewEntry(id, document.CreatedAt, request.Format, artifact.Id, artifact.Sha256, artifact.Bytes, document.Left, document.Right,
                document.Query, document.IncludeRungNumbers, document.IncludeXml, document.IncludeLadder, document.TotalChanges, rows.Length, Hash(catalogBytes));
            ReviewWrite("comparison-reviews", id, entry); return new(artifact, entry);
        }
        finally { gate.Release(); }
    }
    public async Task<ComparisonReviewPage> ComparisonReviewsAsync(string? query = null, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (offset is < 0 or > ComparisonReviews.MaximumHistory || limit is < 1 or > 100 || query?.Length > 256)
            throw new OperationException("invalid_comparison_review", "Use offset 0..200, limit 1..100 and query up to 256 characters.");
        await gate.WaitAsync(ct);
        try
        {
            var dir = ComparisonReviewDirectory(); var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : [];
            if (files.Length > ComparisonReviews.MaximumHistory) throw new OperationException("comparison_review_limit", "Review history exceeds 200 entries.");
            var entries = files.Select(file => { RejectLinks(file); return Read<ComparisonReviewEntry>(file); })
                .Where(e => string.IsNullOrEmpty(query) || new[] { e.Left.Name, e.Right.Name, e.Left.Sha256, e.Right.Sha256, e.Query ?? "", e.Id }.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
            return new(entries.Length, offset, limit, entries.Skip(offset).Take(limit).ToArray());
        }
        finally { gate.Release(); }
    }
    public async Task<ComparisonReviewEntry> ComparisonReviewAsync(string id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            return VerifiedComparisonReview(id).Entry;
        }
        finally { gate.Release(); }
    }
    public async Task<object> ForgetComparisonReviewAsync(string id, ComparisonReviewDeleteRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var entry = ReadComparisonReview(id);
            if (!entry.Sha256.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Review hash changed; refresh history before forgetting it.");
            File.Delete(ExistingReviewPath("comparison-reviews", id));
            lock (auditGate) File.AppendAllText(Path.Combine(state, "audit.jsonl"), JsonSerializer.Serialize(new AuditEvent(DateTimeOffset.UtcNow,
                "comparison-reviews_forget", "review:" + id, entry.Sha256, null, "Forgot local history entry; its artifact and projects are preserved.")) + "\n");
            return new { forgotten = true, artifactPreserved = true };
        }
        finally { gate.Release(); }
    }
}
