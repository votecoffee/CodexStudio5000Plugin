using System.Text.Json;
using System.Text.Json.Nodes;

namespace Studio5000.Core;

public sealed record TrendPoint(DateTimeOffset At, double? Value, string Quality, string? ExactValue = null);
public sealed record TrendSeries(string Key, bool Boolean, TrendPoint[] Points);
public sealed record TrendGap(DateTimeOffset From, DateTimeOffset To, string Reason);
public sealed record WatchTrend(TrendSeries[] Series, TrendGap[] Gaps, bool SeriesLimitReached);

public static class WatchTrends
{
    // Display projections use doubles; the recording and CSV retain exact JSON integer values.
    public static WatchTrend Build(WatchRecord[] records, int intervalMs, DateTimeOffset from, DateTimeOffset to, bool stale)
    {
        var keys = new Dictionary<string, bool>(StringComparer.Ordinal); var limit = false;
        Dictionary<string, (double Number, bool Boolean, string Exact)> Flatten(WatchSnapshot sample)
        {
            var values = new Dictionary<string, (double, bool, string)>(StringComparer.Ordinal); var budget = 4096;
            void Walk(string key, JsonNode? node, int depth)
            {
                if (--budget < 0 || depth > 16) { limit = true; return; }
                if (node is JsonObject obj) { foreach (var item in obj) Walk(key + "." + item.Key, item.Value, depth + 1); return; }
                if (node is JsonArray array) { for (var i = 0; i < array.Count && budget > 0; i++) Walk($"{key}[{i}]", array[i], depth + 1); return; }
                if (node is null) return;
                var value = JsonSerializer.SerializeToElement(node);
                var boolean = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                double number;
                if (boolean) number = value.GetBoolean() ? 1 : 0;
                else if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out number) || !double.IsFinite(number)) return;
                if (!keys.ContainsKey(key))
                {
                    if (keys.Count >= 128) { limit = true; return; }
                    keys.Add(key, boolean);
                }
                values[key] = (number, boolean, node.ToJsonString());
            }
            foreach (var value in sample.Values.Where(v => v.Quality == "good")) Walk(value.Key, value.Value, 0);
            return values;
        }
        var samples = records.Where(r => r.Snapshot is not null).ToDictionary(r => r.Index, r => Flatten(r.Snapshot!));
        var points = keys.ToDictionary(k => k.Key, _ => new List<TrendPoint>(), StringComparer.Ordinal);
        var gaps = new List<TrendGap>(); DateTimeOffset? last = null, gapStart = from; var reason = "No recorded samples";
        string? session = null;
        var threshold = TimeSpan.FromMilliseconds(Math.Max(intervalMs * 2L, 1000));
        void Break(DateTimeOffset at, string quality)
        { foreach (var series in points.Values) series.Add(new(at, null, quality)); }
        foreach (var record in records)
        {
            if (record.Snapshot is null)
            {
                Break(record.At, record.Kind); gapStart ??= last ?? record.At; reason = record.Detail ?? record.Kind;
                continue;
            }
            var sample = record.Snapshot; var values = samples[record.Index];
            if (session is not null && session != record.Session) { Break(record.At, "session_change"); gapStart ??= last ?? record.At; reason = "Different polling session"; }
            if (last is not null && (record.At - last > threshold || record.At <= last))
            { Break(record.At, "communication_gap"); gapStart ??= last; reason = "Missing or nonmonotonic sample times"; }
            if (gapStart is not null) { if (record.At > gapStart) gaps.Add(new(gapStart.Value, record.At, reason)); gapStart = null; }
            foreach (var key in keys.Keys)
            {
                var value = values.GetValueOrDefault(key);
                var good = values.ContainsKey(key);
                points[key].Add(new(record.At, good ? value.Number : null, good ? "good" : "bad_or_unavailable", good ? value.Exact : null));
            }
            if (sample.Values.Any(v => v.Quality != "good")) gaps.Add(new(sample.StartedAt, sample.CompletedAt, "Bad or unavailable tag values"));
            last = record.At; session = record.Session;
        }
        if (gapStart is not null && to > gapStart) gaps.Add(new(gapStart.Value, to, reason));
        else if (last is not null && to - last > threshold) gaps.Add(new(last.Value, to, stale ? "Stale / polling stopped" : "No new samples"));
        return new(keys.Select(k => new TrendSeries(k.Key, k.Value, points[k.Key].ToArray())).ToArray(), gaps.ToArray(), limit);
    }
}
