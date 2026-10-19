using System.Text;
using System.Text.Json;

namespace Studio5000.Core;

public sealed record ComparisonChangeIdentity(string Id, int Ordinal, string Kind, string Path, string Change,
    string? LeftXPath, string? RightXPath, string? LeftSha256, string? RightSha256);
public sealed record ComparisonChangeCatalog(int SchemaVersion, string ReportSha256, ComparisonChangeIdentity[] Changes);
public sealed record ComparisonAnnotation(string ChangeId, string Status, string Note);
public sealed record ComparisonAnnotationState(int SchemaVersion, string ReviewId, string ReportSha256, int Revision, DateTimeOffset? UpdatedAt, ComparisonAnnotation[] Items);
public sealed record ComparisonAnnotationUpdateRequest(string ExpectedReportSha256, int ExpectedRevision, ComparisonAnnotation[] Changes);
public sealed record ComparisonAnnotationExportRequest(string ExpectedReportSha256, int ExpectedRevision);
public sealed record AnnotatedComparisonChange(ComparisonChangeIdentity Change, string Status, string Note);
public sealed record ComparisonAnnotationSummary(string ReviewId, string ReportSha256, int Revision, DateTimeOffset? UpdatedAt,
    int Total, int Unreviewed, int Reviewed, int NeedsAttention, string Boundary);
public sealed record ComparisonAnnotationPage(ComparisonAnnotationSummary Summary, int Matching, int Offset, int Limit, AnnotatedComparisonChange[] Changes);
public sealed record ComparisonAnnotationExportDocument(int SchemaVersion, string ApplicationVersion, DateTimeOffset CreatedAt,
    ComparisonReviewEntry Report, ComparisonAnnotationSummary Summary, AnnotatedComparisonChange[] Notes);

public static class ComparisonAnnotations
{
    public const int MaximumNoteLength = 2000, MaximumStateBytes = 2 * 1024 * 1024, MaximumBatch = 100;
    public const string Boundary = "Local review tracking for this exact immutable report. Unlisted changes remain unreviewed. Notes and reviewed status do not establish native acceptance or authorize project/controller actions.";
    public static void Validate(ComparisonAnnotation annotation)
    {
        if (annotation is null || string.IsNullOrEmpty(annotation.ChangeId) || annotation.Status is not ("unreviewed" or "reviewed" or "needs-attention") || annotation.Note is null ||
            annotation.Note.Length > MaximumNoteLength || annotation.Note.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new OperationException("invalid_comparison_annotation", "Use an exact change ID, status unreviewed/reviewed/needs-attention and a note up to 2000 characters without control characters other than newlines/tabs.");
    }
    internal static ComparisonChangeCatalog Catalog(string reportHash, ComparisonReviewChange[] changes) => new(1, reportHash,
        changes.Select((row, index) =>
        {
            var c = row.Difference;
            var id = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { reportHash, ordinal = index, c.Kind, c.Path, c.Change, c.LeftXPath, c.RightXPath, c.LeftSha256, c.RightSha256 }, ProjectService.Json));
            return new ComparisonChangeIdentity(id, index, c.Kind, c.Path, c.Change, c.LeftXPath, c.RightXPath, c.LeftSha256, c.RightSha256);
        }).ToArray());
}

public sealed partial class ProjectService
{
    private (ComparisonReviewEntry Entry, string Path) VerifiedComparisonReview(string id)
    {
        var entry = ReadComparisonReview(id); var (artifact, path) = Download(entry.ArtifactId);
        if (artifact.Sha256 != entry.Sha256 || artifact.Format != entry.Format || artifact.Bytes != entry.Bytes)
            throw new OperationException("artifact_tampered", "Review metadata differs from its immutable artifact.");
        return (entry, path);
    }
    private static byte[] BoundedAnnotationFile(string path, int maximum)
    {
        RejectLinks(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new OperationException("state_tampered", "Saved review data exceeds its size limit.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        if (bytes.Length > maximum) throw new OperationException("state_tampered", "Saved review data exceeds its size limit.");
        return bytes.ToArray();
    }
    private ComparisonChangeCatalog AnnotationCatalog(ComparisonReviewEntry entry, string reportPath)
    {
        ComparisonChangeCatalog catalog;
        if (entry.CatalogSha256 is { } hash)
        {
            var path = Path.Combine(ArtifactDir(entry.ArtifactId), "changes.json");
            if (!File.Exists(path)) throw new OperationException("state_tampered", "Saved report change catalog is missing.");
            var bytes = BoundedAnnotationFile(path, ComparisonReviews.MaximumBytes);
            if (!Hash(bytes).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new OperationException("state_tampered", "Saved report change catalog hash mismatch.");
            catalog = JsonSerializer.Deserialize<ComparisonChangeCatalog>(bytes, Json) ?? throw new OperationException("state_tampered", "Saved report change catalog is empty.");
        }
        else if (entry.Format == "json")
        {
            var report = JsonSerializer.Deserialize<ComparisonReviewDocument>(BoundedAnnotationFile(reportPath, ComparisonReviews.MaximumBytes), Json);
            if (report is not { SchemaVersion: 1, Changes: not null }) throw new OperationException("state_tampered", "Legacy report schema is not supported for annotation.");
            catalog = ComparisonAnnotations.Catalog(entry.Sha256, report.Changes);
        }
        else throw new OperationException("comparison_annotations_unavailable", "This older HTML report has no saved change catalog. Export a new comparison report to annotate its recorded changes.");
        if (catalog.SchemaVersion != 1 || !string.Equals(catalog.ReportSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) || catalog.Changes is null ||
            catalog.Changes.Length != entry.IncludedChanges || catalog.Changes.Length > ComparisonReviews.MaximumChanges ||
            catalog.Changes.Where((c, i) => c is null || c.Ordinal != i || string.IsNullOrEmpty(c.Id)).Any() ||
            catalog.Changes.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Changes.Length)
            throw new OperationException("state_tampered", "Saved change catalog differs from its report.");
        return catalog;
    }
    private ComparisonAnnotationState AnnotationState(ComparisonReviewEntry entry, ComparisonChangeCatalog catalog)
    {
        var path = ReviewPath("comparison-annotations", entry.Id);
        if (!File.Exists(path)) return new(1, entry.Id, entry.Sha256, 0, null, []);
        var state = JsonSerializer.Deserialize<ComparisonAnnotationState>(BoundedAnnotationFile(path, ComparisonAnnotations.MaximumStateBytes), Json);
        var ids = catalog.Changes.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (state is not { SchemaVersion: 1, Revision: >= 1, UpdatedAt: not null, Items: not null } || state.ReviewId != entry.Id ||
            !string.Equals(state.ReportSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) || state.Items.Length > catalog.Changes.Length ||
            state.Items.Any(a => a is null || !ids.Contains(a.ChangeId)) || state.Items.Select(a => a.ChangeId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Items.Length)
            throw new OperationException("state_tampered", "Saved annotations differ from their report.");
        foreach (var annotation in state.Items) ComparisonAnnotations.Validate(annotation);
        return state;
    }
    private static ComparisonAnnotationSummary AnnotationSummary(ComparisonReviewEntry entry, ComparisonAnnotationState state)
    {
        var reviewed = state.Items.Count(a => a.Status == "reviewed"); var attention = state.Items.Count(a => a.Status == "needs-attention");
        return new(entry.Id, entry.Sha256, state.Revision, state.UpdatedAt, entry.IncludedChanges, entry.IncludedChanges - reviewed - attention, reviewed, attention, ComparisonAnnotations.Boundary);
    }
    private static AnnotatedComparisonChange Annotated(ComparisonChangeIdentity change, Dictionary<string, ComparisonAnnotation> notes) =>
        notes.TryGetValue(change.Id, out var a) ? new(change, a.Status, a.Note) : new(change, "unreviewed", "");
    public async Task<ComparisonAnnotationPage> ComparisonAnnotationsAsync(string id, string? query = null, string? status = null, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (offset is < 0 or > ComparisonReviews.MaximumChanges || limit is < 1 or > 100 || query?.Length > 256 || status is not (null or "all" or "unreviewed" or "reviewed" or "needs-attention"))
            throw new OperationException("invalid_comparison_annotation", "Use offset 0..5000, limit 1..100, query up to 256 characters and a supported status filter.");
        await gate.WaitAsync(ct);
        try
        {
            var (entry, reportPath) = VerifiedComparisonReview(id); var catalog = AnnotationCatalog(entry, reportPath); var state = AnnotationState(entry, catalog);
            var notes = state.Items.ToDictionary(a => a.ChangeId, StringComparer.OrdinalIgnoreCase);
            var selected = catalog.Changes.Select(c => Annotated(c, notes))
                .Where(c => (status is null or "all" || c.Status == status) && (string.IsNullOrEmpty(query) ||
                    new[] { c.Change.Kind, c.Change.Path, c.Change.Change, c.Note }.Any(text => text.Contains(query, StringComparison.OrdinalIgnoreCase)))).ToArray();
            var page = new ComparisonAnnotationPage(AnnotationSummary(entry, state), selected.Length, offset, limit, selected.Skip(offset).Take(limit).ToArray());
            if (JsonSerializer.SerializeToUtf8Bytes(page, Json).Length > 4 * 1024 * 1024) throw new OperationException("comparison_annotation_limit", "Annotation page exceeds 4 MiB; use a smaller page.");
            return page;
        }
        finally { gate.Release(); }
    }
    public async Task<ComparisonAnnotationSummary> UpdateComparisonAnnotationsAsync(string id, ComparisonAnnotationUpdateRequest request, CancellationToken ct = default)
    {
        if (request.Changes is null || request.Changes.Length is < 1 or > ComparisonAnnotations.MaximumBatch)
            throw new OperationException("invalid_comparison_annotation", "Save 1..100 distinct change annotations per request.");
        foreach (var annotation in request.Changes) ComparisonAnnotations.Validate(annotation);
        if (request.Changes.Select(a => a.ChangeId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Changes.Length)
            throw new OperationException("invalid_comparison_annotation", "Each change ID may occur only once in a batch.");
        await gate.WaitAsync(ct);
        try
        {
            var (entry, reportPath) = VerifiedComparisonReview(id); var catalog = AnnotationCatalog(entry, reportPath); var state = AnnotationState(entry, catalog);
            ExpectAnnotations(entry, state, request.ExpectedReportSha256, request.ExpectedRevision);
            if (state.Revision == int.MaxValue) throw new OperationException("comparison_annotation_limit", "Annotation revision limit reached.");
            var ids = catalog.Changes.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase); var notes = state.Items.ToDictionary(a => a.ChangeId, StringComparer.OrdinalIgnoreCase);
            foreach (var annotation in request.Changes)
            {
                if (!ids.TryGetValue(annotation.ChangeId, out var change)) throw new OperationException("invalid_comparison_annotation", "A change ID does not belong to this report.");
                if (annotation.Status == "unreviewed" && annotation.Note.Length == 0) notes.Remove(change.Id);
                else notes[change.Id] = annotation with { ChangeId = change.Id };
            }
            var updated = state with { Revision = state.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow, Items = notes.Values.OrderBy(a => ids[a.ChangeId].Ordinal).ToArray() };
            if (JsonSerializer.SerializeToUtf8Bytes(updated, Json).Length > ComparisonAnnotations.MaximumStateBytes)
                throw new OperationException("comparison_annotation_limit", "Saved annotations exceed 2 MiB; shorten notes before saving.");
            ct.ThrowIfCancellationRequested(); ReviewWrite("comparison-annotations", entry.Id, updated); return AnnotationSummary(entry, updated);
        }
        finally { gate.Release(); }
    }
    private static void ExpectAnnotations(ComparisonReviewEntry entry, ComparisonAnnotationState state, string hash, int revision)
    {
        if (!entry.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase) || state.Revision != revision)
            throw new OperationException("stale_revision", "Report hash or annotation revision changed; reload notes before saving or exporting.");
    }
    public async Task<Artifact> ExportComparisonAnnotationsAsync(string id, ComparisonAnnotationExportRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var (entry, reportPath) = VerifiedComparisonReview(id); var catalog = AnnotationCatalog(entry, reportPath); var state = AnnotationState(entry, catalog);
            ExpectAnnotations(entry, state, request.ExpectedReportSha256, request.ExpectedRevision);
            var notes = state.Items.ToDictionary(a => a.ChangeId, StringComparer.OrdinalIgnoreCase);
            var document = new ComparisonAnnotationExportDocument(1, WorkspaceIdentity.Version, DateTimeOffset.UtcNow, entry, AnnotationSummary(entry, state),
                catalog.Changes.Where(c => notes.ContainsKey(c.Id)).Select(c => Annotated(c, notes)).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
            if (bytes.Length > ComparisonReviews.MaximumBytes) throw new OperationException("comparison_annotation_limit", "Notes export exceeds 16 MiB.");
            ct.ThrowIfCancellationRequested(); var artifactId = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(artifactId); Directory.CreateDirectory(dir);
            AtomicBytes(Path.Combine(dir, "project.json"), bytes);
            var artifact = new Artifact(artifactId, entry.Id, "json", Hash(bytes), bytes.Length, ComparisonAnnotations.Boundary);
            Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
        }
        finally { gate.Release(); }
    }
}
