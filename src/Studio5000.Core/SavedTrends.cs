using System.Text.Json;

namespace Studio5000.Core;

public sealed record SavedTrendRequest(string Name, string WatchId, int WatchRevision, string[] Series, int WindowMinutes = 15, string? Id = null, int? ExpectedRevision = null);
public sealed record SavedTrend(string Id, int Revision, string Name, string WatchId, int WatchRevision, string ProjectSha256, string[] Series, int WindowMinutes);
public sealed record TrendAnalyzeRequest(string TrendId, int ExpectedRevision, DateTimeOffset? From = null, DateTimeOffset? To = null, long AfterIndex = 0, int Limit = 5000);
public sealed record TrendExportRequest(TrendAnalyzeRequest Analysis, string ExpectedResultSha256);
public sealed record SavedTrendSummary(string Id, int Revision, string Name, string WatchId, int WatchRevision, int SeriesCount);
public sealed record TrendStatistic(string Key, int GoodSamples, int MissingSamples, double? Minimum, double? Maximum, double? Mean, string? FirstExact, string? LastExact);
public sealed record TrendAnalysis(SavedTrend Definition, DateTimeOffset From, DateTimeOffset To, WatchTrend Trend, TrendStatistic[] Statistics, bool HasMore, long NextIndex, string ResultSha256, string Boundary);

public sealed partial class ProjectService
{
    public SavedTrendSummary[] SavedTrends()
    { var dir = ReviewDirectory("trend-views"); return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(f => { RejectLinks(f); var t = Read<SavedTrend>(f); return new SavedTrendSummary(t.Id, t.Revision, t.Name, t.WatchId, t.WatchRevision, t.Series.Length); }).OrderBy(t => t.Name).ToArray() : []; }
    public SavedTrend GetSavedTrend(string id) => Read<SavedTrend>(ExistingReviewPath("trend-views", id));
    public async Task<SavedTrend> SaveTrendAsync(SavedTrendRequest request, CancellationToken ct = default)
    {
        SavedReviewValidation.Name(request.Name);
        if (request.Series is null || request.Series.Length is < 1 or > 128 || request.Series.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 512) || request.Series.Distinct(StringComparer.Ordinal).Count() != request.Series.Length || request.WindowMinutes is < 1 or > 10080)
            throw new OperationException("trend_settings", "Choose 1..128 distinct series and a window of 1..10080 minutes.");
        await gate.WaitAsync(ct);
        try
        {
            WatchDefinition def; lock (watchLock) def = Watch(request.WatchId).Recorder.Definition(request.WatchRevision);
            if (request.Series.Any(s => !def.Settings.Tags.Any(t => s == t.Key || s.StartsWith(t.Key + ".", StringComparison.Ordinal) || s.StartsWith(t.Key + "[", StringComparison.Ordinal)))) throw new OperationException("trend_series", "A selected series is absent from the bound watch definition.");
            var (id, revision) = ReviewRevision("trend-views", request.Id, request.ExpectedRevision);
            var trend = new SavedTrend(id, revision, request.Name.Trim(), def.Id, def.Revision, def.Settings.ExpectedSha256, request.Series, request.WindowMinutes); ReviewWrite("trend-views", id, trend); return trend;
        }
        finally { gate.Release(); }
    }
    public TrendAnalysis AnalyzeTrend(TrendAnalyzeRequest request)
    {
        var definition = Read<SavedTrend>(ExistingReviewPath("trend-views", request.TrendId));
        if (definition.Revision != request.ExpectedRevision) throw new OperationException("stale_revision", "Saved trend view changed; reload before gathering results.");
        var to = request.To ?? DateTimeOffset.UtcNow; var from = request.From ?? to.AddMinutes(-definition.WindowMinutes);
        var page = QueryRecording(definition.WatchId, new(definition.WatchRevision, from, to, request.AfterIndex, request.Limit));
        if (page.Definition!.Settings.ExpectedSha256 != definition.ProjectSha256) throw new OperationException("state_tampered", "Trend source hash differs from its recorded watch revision.");
        var trend = WatchTrends.Build(page.Records, page.Definition.Settings.IntervalMs, from, to, page.Stale,
            key => definition.Series.Any(selected => key == selected || key.StartsWith(selected + ".", StringComparison.Ordinal) || key.StartsWith(selected + "[", StringComparison.Ordinal)));
        if (trend.Series.Length > 128 || JsonSerializer.SerializeToUtf8Bytes(trend, Json).Length > 6 * 1024 * 1024) throw new OperationException("trend_limit", "Trend exceeds 128 scalar series or 6 MiB; select fewer series or a smaller page.");
        var stats = trend.Series.Select(s =>
        {
            var good = s.Points.Where(p => p.Quality == "good" && p.Value is not null).ToArray(); var values = good.Select(p => p.Value!.Value).ToArray();
            var mean = 0.0; for (var i = 0; i < values.Length; i++) mean = mean * ((double)i / (i + 1)) + values[i] / (i + 1);
            return new TrendStatistic(s.Key, good.Length, s.Points.Count(p => p.Quality == "bad_or_unavailable"), values.Length == 0 ? null : values.Min(), values.Length == 0 ? null : values.Max(), values.Length == 0 || !double.IsFinite(mean) ? null : mean, good.FirstOrDefault()?.ExactValue, good.LastOrDefault()?.ExactValue);
        }).ToArray();
        var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { definition, from, to, page.Records, page.HasMore, trend, stats }, Json));
        return new(definition, from, to, trend, stats, page.HasMore, page.NextIndex, hash, "Results use this exact recorded watch/source revision. Numeric display/aggregates use doubles; first/last JSON values retain exact integer text. Gaps and bad quality remain explicit. HasMore means statistics cover only the returned bounded sample window, not the complete requested range. Polling is never started by creating, editing or analyzing a trend view.");
    }
    public Artifact ExportTrendAnalysis(TrendExportRequest request)
    {
        if (request.Analysis.From is null || request.Analysis.To is null) throw new OperationException("trend_export_window", "Pin the analyzed From/To timestamps before exporting results.");
        var result = AnalyzeTrend(request.Analysis);
        if (!result.ResultSha256.Equals(request.ExpectedResultSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Trend records/settings differ from the analyzed results; gather them again.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json); if (bytes.Length > 8 * 1024 * 1024) throw new OperationException("trend_limit", "Trend export exceeds 8 MiB; choose a smaller page.");
        var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project.json"), bytes);
        var artifact = new Artifact(id, result.Definition.Id, "json", Hash(bytes), bytes.Length, result.Boundary); Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
    }
}
