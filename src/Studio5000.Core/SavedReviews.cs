using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio5000.Core;

public sealed record ReviewSummary(string Id, int Revision, string Name, DateTimeOffset UpdatedAt);
public sealed record ReviewDeleteRequest(int ExpectedRevision);
public sealed record ComparisonBookmark(string? LeftRoutineXPath, string? RightRoutineXPath, int RungOffset = 0,
    bool ChangesOnly = false, string? RoutineQuery = null);
public sealed record ComparisonSessionSaveRequest(string Name, ProjectCompareRequest Comparison,
    ComparisonBookmark? Bookmark = null, string? Id = null, int? ExpectedRevision = null);
public sealed record ComparisonSession(string Id, int Revision, DateTimeOffset UpdatedAt, string Name,
    ProjectCompareRequest Comparison, ComparisonBookmark? Bookmark);
public sealed record ComparisonSessionView(ComparisonSession Session, bool CanResume, string[] Issues);
public sealed record MergeTagPair(string SourceTag, string DestinationTag);
public sealed record MergeMappingTemplate(int SchemaVersion, string Name, MergeTagPair[] Tags);
public sealed record MergeMappingSaveRequest(JsonElement Template, string? Id = null, int? ExpectedRevision = null);
public sealed record SavedMergeMapping(string Id, int Revision, DateTimeOffset UpdatedAt, MergeMappingTemplate Template);
public sealed record MergeMappingBindRequest(string MappingId, int ExpectedRevision, string SourceProjectId, string SourceSha256,
    string DestinationProjectId, string DestinationSha256, string? SourceProgram = null, string? DestinationProgram = null);
public sealed record MergeMappingBinding(SavedMergeMapping Mapping, TagValueMergeRequest Proposed, TagValueMergeReport Preview);

public static class MergeMappingTemplates
{
    public const int MaximumBytes = 128 * 1024;
    public static byte[] Bytes(MergeMappingTemplate template) { Validate(template); return JsonSerializer.SerializeToUtf8Bytes(template, ProjectService.Json); }
    private static readonly JsonSerializerOptions Strict = new(ProjectService.Json)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    public static MergeMappingTemplate Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumBytes)
            throw new OperationException("invalid_merge_mapping", "Use a mapping JSON object up to 128 KiB.");
        void Keys(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in v.EnumerateObject()) { if (!seen.Add(p.Name)) throw new JsonException("Duplicate property."); Keys(p.Value); }
            }
            else if (v.ValueKind == JsonValueKind.Array) foreach (var item in v.EnumerateArray()) Keys(item);
        }
        MergeMappingTemplate template;
        try { Keys(value); template = value.Deserialize<MergeMappingTemplate>(Strict) ?? throw new JsonException("Empty mapping."); }
        catch (JsonException ex) { throw new OperationException("invalid_merge_mapping", "Invalid mapping fields: " + ex.Message); }
        Validate(template); return template;
    }
    public static void Validate(MergeMappingTemplate template)
    {
        SavedReviewValidation.Name(template.Name);
        if (template.SchemaVersion != 1 || template.Tags is null || template.Tags.Length is < 1 or > 256 || template.Tags.Any(t => t is null))
            throw new OperationException("invalid_merge_mapping", "Use schemaVersion 1 and 1..256 whole-tag name pairs.");
        foreach (var tag in template.Tags) { WatchValidation.Identifier(tag.SourceTag); WatchValidation.Identifier(tag.DestinationTag); }
        if (template.Tags.Select(t => t.DestinationTag).Distinct(StringComparer.OrdinalIgnoreCase).Count() != template.Tags.Length)
            throw new OperationException("invalid_merge_mapping", "Each destination tag may occur only once.");
        if (JsonSerializer.SerializeToUtf8Bytes(template, ProjectService.Json).Length > MaximumBytes)
            throw new OperationException("invalid_merge_mapping", "Canonical mapping exceeds 128 KiB.");
    }
}
internal static class SavedReviewValidation
{
    internal static void Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl))
            throw new OperationException("invalid_review_name", "Use a nonempty display name up to 120 characters without control characters.");
    }
}

public sealed partial class ProjectService
{
    private string ReviewDirectory(string kind) { var path = Path.Combine(state, kind); RejectLinks(path); return path; }
    private string ReviewPath(string kind, string id) { var path = Path.Combine(ReviewDirectory(kind), Id(id) + ".json"); RejectLinks(path); return path; }
    private void ReviewWrite<T>(string kind, string id, T value)
    {
        Directory.CreateDirectory(ReviewDirectory(kind)); Save(ReviewPath(kind, id), value);
        lock (auditGate) File.AppendAllText(Path.Combine(state, "audit.jsonl"), JsonSerializer.Serialize(
            new AuditEvent(DateTimeOffset.UtcNow, kind + "_save", "review:" + id, null, Hash(JsonSerializer.SerializeToUtf8Bytes(value, Json)), "Saved local review definition.")) + "\n");
    }
    private (string Id, int Revision) ReviewRevision(string kind, string? id, int? expected)
    {
        if (id is null)
        {
            if (expected is not null) throw new OperationException("invalid_review", "An expected revision requires an existing definition ID.");
            if (Directory.Exists(ReviewDirectory(kind)) && Directory.GetFiles(ReviewDirectory(kind), "*.json").Length >= 200)
                throw new OperationException("review_limit", "At most 200 saved definitions of each kind are supported.");
            return (Guid.NewGuid().ToString("N"), 0);
        }
        using var json = JsonDocument.Parse(File.ReadAllText(ExistingReviewPath(kind, id)));
        var revision = json.RootElement.GetProperty("revision").GetInt32();
        if (revision != expected) throw new OperationException("stale_revision", "Saved definition changed; refresh before updating or deleting.");
        if (revision == int.MaxValue) throw new OperationException("review_limit", "Definition revision limit reached.");
        return (Id(id), revision + 1);
    }
    private string ExistingReviewPath(string kind, string id)
    { var path = ReviewPath(kind, id); if (!File.Exists(path)) throw new OperationException("not_found", "Saved definition does not exist."); return path; }
    public async Task<ReviewSummary[]> SavedReviewsAsync(string kind, CancellationToken ct = default)
    {
        if (kind is not ("comparison-sessions" or "merge-mappings")) throw new OperationException("invalid_review", "Unknown review kind.");
        await gate.WaitAsync(ct);
        try
        {
            var dir = ReviewDirectory(kind); if (!Directory.Exists(dir)) return [];
            var files = Directory.GetFiles(dir, "*.json");
            if (files.Length > 200) throw new OperationException("review_limit", "Saved definition count exceeds 200.");
            return files.Select(file =>
            {
                RejectLinks(file);
                return kind == "comparison-sessions" ? Summary(Read<ComparisonSession>(file)) : Summary(Read<SavedMergeMapping>(file));
            }).OrderByDescending(s => s.UpdatedAt).ToArray();
        }
        finally { gate.Release(); }
    }
    private static ReviewSummary Summary(ComparisonSession s) => new(s.Id, s.Revision, s.Name, s.UpdatedAt);
    private static ReviewSummary Summary(SavedMergeMapping m) => new(m.Id, m.Revision, m.Template.Name, m.UpdatedAt);
    public async Task<ComparisonSession> SaveComparisonSessionAsync(ComparisonSessionSaveRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            SavedReviewValidation.Name(request.Name);
            if (request.Comparison is null) throw new OperationException("invalid_review", "A pinned comparison is required.");
            _ = CompareProjects(request.Comparison);
            if (request.Bookmark is { } b)
            {
                if (b.RoutineQuery?.Length > 256) throw new OperationException("invalid_review", "Routine filter exceeds 256 characters.");
                var c = request.Comparison;
                var ladder = CompareLadder(new(c.LeftProjectId, c.LeftSha256, c.RightProjectId, c.RightSha256, b.LeftRoutineXPath, b.RightRoutineXPath,
                    b.RungOffset, 1, c.IncludeRungNumbers, b.ChangesOnly));
                if (b.RungOffset >= Math.Max(1, ladder.Total)) throw new OperationException("invalid_review", "Bookmark is outside the aligned rung rows.");
            }
            var (id, revision) = ReviewRevision("comparison-sessions", request.Id, request.ExpectedRevision);
            var saved = new ComparisonSession(id, revision, DateTimeOffset.UtcNow, request.Name.Trim(), request.Comparison, request.Bookmark);
            ReviewWrite("comparison-sessions", id, saved); return saved;
        }
        finally { gate.Release(); }
    }
    public async Task<ComparisonSessionView> GetComparisonSessionAsync(string id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var saved = Read<ComparisonSession>(ExistingReviewPath("comparison-sessions", id)); var issues = new List<string>();
            void Check(string side, string projectId, string hash)
            {
                try { var current = Get(projectId); if (current.Kind != "logix" || !current.CurrentSha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) issues.Add(side + " snapshot changed; select current snapshots and compare explicitly before saving a new revision."); }
                catch (OperationException ex) { issues.Add(side + ": " + ex.Code + ": " + ex.Message); }
                catch (IOException) { issues.Add(side + ": saved snapshot file is unavailable."); }
            }
            Check("Left", saved.Comparison.LeftProjectId, saved.Comparison.LeftSha256); Check("Right", saved.Comparison.RightProjectId, saved.Comparison.RightSha256);
            return new(saved, issues.Count == 0, issues.ToArray());
        }
        finally { gate.Release(); }
    }
    public async Task<SavedMergeMapping> SaveMergeMappingAsync(MergeMappingSaveRequest request, CancellationToken ct = default)
    {
        var template = MergeMappingTemplates.Parse(request.Template);
        await gate.WaitAsync(ct);
        try
        {
            var (id, revision) = ReviewRevision("merge-mappings", request.Id, request.ExpectedRevision);
            var saved = new SavedMergeMapping(id, revision, DateTimeOffset.UtcNow, template with { Name = template.Name.Trim() });
            ReviewWrite("merge-mappings", id, saved); return saved;
        }
        finally { gate.Release(); }
    }
    public async Task<SavedMergeMapping> GetMergeMappingAsync(string id, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return Read<SavedMergeMapping>(ExistingReviewPath("merge-mappings", id)); } finally { gate.Release(); } }
    public async Task<MergeMappingBinding> BindMergeMappingAsync(MergeMappingBindRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var mapping = Read<SavedMergeMapping>(ExistingReviewPath("merge-mappings", request.MappingId));
            if (mapping.Revision != request.ExpectedRevision) throw new OperationException("stale_revision", "Mapping changed; refresh before binding.");
            MergeMappingTemplates.Validate(mapping.Template);
            var proposed = new TagValueMergeRequest(request.SourceProjectId, request.SourceSha256, request.DestinationProjectId, request.DestinationSha256,
                mapping.Template.Tags.Select(t => new TagValueMap(t.SourceTag, request.SourceProgram, t.DestinationTag, request.DestinationProgram)).ToArray(), request.SourceProgram, request.DestinationProgram);
            return new(mapping, proposed, BuildTagValueMerge(proposed).Report);
        }
        finally { gate.Release(); }
    }
    public async Task<object> DeleteSavedReviewAsync(string kind, string id, ReviewDeleteRequest request, CancellationToken ct = default)
    {
        if (kind is not ("comparison-sessions" or "merge-mappings")) throw new OperationException("invalid_review", "Unknown review kind.");
        await gate.WaitAsync(ct);
        try
        {
            _ = ReviewRevision(kind, id, request.ExpectedRevision); File.Delete(ExistingReviewPath(kind, id));
            lock (auditGate) File.AppendAllText(Path.Combine(state, "audit.jsonl"), JsonSerializer.Serialize(new AuditEvent(DateTimeOffset.UtcNow,
                kind + "_delete", "review:" + id, null, null, "Deleted local review definition.")) + "\n");
            return new { deleted = true };
        }
        finally { gate.Release(); }
    }
}
