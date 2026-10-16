using System.Text.Json.Nodes;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class WatchTrendTests
{
    private readonly DateTimeOffset start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private WatchRecord Sample(int index, int second, string json = "8", string quality = "good", string session = "one") => new(index, 0, session, start.AddSeconds(second), "sample",
        new(index, start.AddSeconds(second).AddMilliseconds(-10), start.AddSeconds(second), "Offline", [new("value", "Record", "WatchProgram", "DINT", JsonNode.Parse(json), quality)]));
    [Fact]
    public void DisconnectBadQualityAndRecordingPausesBreakLinesAndShowStaleTail()
    {
        var trend = WatchTrends.Build([Sample(1, 0), Sample(2, 1, quality: "bad"), new(3, 0, "one", start.AddSeconds(2), "recording_disabled"), Sample(4, 5, session: "two")], 1000, start, start.AddSeconds(10), true);
        var series = Assert.Single(trend.Series);
        Assert.Contains(series.Points, p => p.Value is null && p.Quality == "bad_or_unavailable");
        Assert.Contains(series.Points, p => p.Value is null && p.Quality == "recording_disabled");
        Assert.Contains(trend.Gaps, g => g.From <= start.AddSeconds(2) && g.To == start.AddSeconds(5));
        Assert.Contains(trend.Gaps, g => g.Reason.Contains("Stale"));
        Assert.Equal(2, series.Points.Count(p => p.Value is not null));
    }
    [Fact]
    public void ProjectsNestedNumericAndBoolLeavesButKeepsExactLargeIntegers()
    {
        var trend = WatchTrends.Build([Sample(1, 0, "{\"Count\":9223372036854775807,\"Enabled\":true,\"Samples\":[1,2],\"Message\":\"hi\"}")], 1000, start, start.AddSeconds(1), false);
        Assert.Equal(4, trend.Series.Length);
        Assert.Equal("9223372036854775807", trend.Series.Single(s => s.Key.EndsWith("Count")).Points[0].ExactValue);
        Assert.True(trend.Series.Single(s => s.Key.EndsWith("Enabled")).Boolean);
        Assert.DoesNotContain(trend.Series, s => s.Key.EndsWith("Message"));
    }
    [Fact]
    public void CapsStructureExpansionWithoutInventingMissingValues()
    {
        var json = "[" + string.Join(',', Enumerable.Range(0, 500)) + "]";
        var trend = WatchTrends.Build([Sample(1, 0, json), Sample(2, 1, quality: "bad")], 1000, start, start.AddSeconds(2), false);
        Assert.True(trend.SeriesLimitReached); Assert.Equal(128, trend.Series.Length);
        Assert.All(trend.Series, s => Assert.Null(s.Points.Last().Value));
    }
}
