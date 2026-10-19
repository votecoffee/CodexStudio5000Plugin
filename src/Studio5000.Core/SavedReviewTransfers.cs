using System.Text.Json;

namespace Studio5000.Core;

public sealed record MergeMappingExportRequest(int ExpectedRevision);
public sealed record MergeMappingImportRequest(JsonElement Template);
public sealed record ComparisonSessionRecoveryRequest(string SessionId, int ExpectedRevision, ProjectCompareRequest Comparison);
public sealed record ComparisonSessionRecoveryReport(ComparisonSession Session, ProjectCompareRequest Proposed,
    string ReviewSha256, ProjectComparisonReport Comparison, string Notice);
public sealed record ComparisonSessionRecoveryApplyRequest(ComparisonSessionRecoveryRequest Preview, string ExpectedReviewSha256);

public sealed partial class ProjectService
{
    public async Task<Artifact> ExportMergeMappingAsync(string id, MergeMappingExportRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var mapping = Read<SavedMergeMapping>(ExistingReviewPath("merge-mappings", id));
            if (mapping.Revision != request.ExpectedRevision) throw new OperationException("stale_revision", "Mapping changed; reload before exporting.");
            var bytes = MergeMappingTemplates.Bytes(mapping.Template);
            var artifactId = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(artifactId); Directory.CreateDirectory(dir);
            AtomicBytes(Path.Combine(dir, "project.json"), bytes);
            var artifact = new Artifact(artifactId, mapping.Id, "json", Hash(bytes), bytes.Length,
                "Portable whole-tag name pairs only. No saved IDs/revisions, project bindings, scopes, paths, routes or values. Review names before sharing.");
            Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
        }
        finally { gate.Release(); }
    }

    // Imports always create a distinct local definition; updating an existing definition requires the separate revision-bound save operation.
    public Task<SavedMergeMapping> ImportMergeMappingAsync(MergeMappingImportRequest request, CancellationToken ct = default) =>
        SaveMergeMappingAsync(new(request.Template), ct);

    private ComparisonSessionRecoveryReport BuildComparisonRecovery(ComparisonSessionRecoveryRequest request)
    {
        var session = Read<ComparisonSession>(ExistingReviewPath("comparison-sessions", request.SessionId));
        if (session.Revision != request.ExpectedRevision) throw new OperationException("stale_revision", "Session changed; reload before recovering it.");
        if (request.Comparison is null) throw new OperationException("invalid_review", "Select explicit current comparison snapshots.");
        var proposed = request.Comparison with { Offset = 0 };
        var comparison = CompareProjects(proposed);
        var reviewHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { session, proposed }, Json));
        return new(session, proposed, reviewHash, comparison,
            "Recovery pins the explicitly selected snapshots and filters in a new session revision. The previous ladder bookmark is cleared; select a rung again after reviewing the current comparison. Preview changes no saved definition or project.");
    }

    public async Task<ComparisonSessionRecoveryReport> PreviewComparisonRecoveryAsync(ComparisonSessionRecoveryRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return BuildComparisonRecovery(request); }
        finally { gate.Release(); }
    }

    public async Task<ComparisonSession> ApplyComparisonRecoveryAsync(ComparisonSessionRecoveryApplyRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var preview = BuildComparisonRecovery(request.Preview);
            if (!preview.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase))
                throw new OperationException("stale_revision", "Recovery differs from the reviewed preview. Review the current snapshots again.");
            var (id, revision) = ReviewRevision("comparison-sessions", preview.Session.Id, preview.Session.Revision);
            var saved = new ComparisonSession(id, revision, DateTimeOffset.UtcNow, preview.Session.Name, preview.Proposed, null);
            ReviewWrite("comparison-sessions", id, saved); return saved;
        }
        finally { gate.Release(); }
    }
}
