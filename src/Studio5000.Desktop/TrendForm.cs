using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class TrendForm : Form
{
    private readonly ServiceClient client;
    private readonly WatchStatus watch;
    private readonly DateTimePicker from = new() { Width = 180, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
    private readonly DateTimePicker to = new() { Width = 180, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
    private readonly CheckBox follow = new() { Text = "Follow last", Checked = true, AutoSize = true };
    private readonly NumericUpDown minutes = new() { Width = 65, Minimum = 1, Maximum = 10080, Value = 15 };
    private readonly NumericUpDown revision = new() { Width = 65, Minimum = 0, Maximum = int.MaxValue };
    private readonly NumericUpDown days = new() { Width = 60, Minimum = 1, Maximum = 365, Value = 7 };
    private readonly NumericUpDown maxMiB = new() { Width = 65, Minimum = 8, Maximum = 256, Value = 64 };
    private readonly CheckedListBox series = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
    private readonly TrendPlot plot = new() { Dock = DockStyle.Fill };
    private readonly DataGridView events = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = false, RowHeadersVisible = false };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 58, Padding = new(8) };
    private readonly Label recording = new() { AutoSize = true, Padding = new(8, 6, 0, 0) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private WatchRecordingStatus? config;
    private WatchTrend? trend;
    private bool busy, fixture, editingRetention;
    public TrendForm(ServiceClient client, WatchStatus watch)
    {
        this.client = client; this.watch = watch;
        Text = "Trends and recording — " + watch.Definition.Settings.Name; Size = new(1380, 900); MinimumSize = new(1160, 730); Font = new("Segoe UI", 10);
        revision.Value = watch.Definition.Revision;
        var header = new Label { Text = "TRENDS & RECORDING  /  " + watch.Definition.Settings.Name + "  /  " + watch.Definition.Settings.Mode.ToUpperInvariant(), Dock = DockStyle.Top,
            Height = 48, BackColor = Color.FromArgb(24, 40, 61), ForeColor = Color.White, Padding = new(12) };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 90, Padding = new(8) };
        controls.Controls.Add(new Label { Text = "From (local)", AutoSize = true }); controls.Controls.Add(from);
        controls.Controls.Add(new Label { Text = "To", AutoSize = true }); controls.Controls.Add(to); controls.Controls.Add(follow); controls.Controls.Add(minutes);
        controls.Controls.Add(new Label { Text = "min • watch revision", AutoSize = true }); controls.Controls.Add(revision);
        Button(controls, "Refresh", RefreshAsync); Button(controls, "Export CSV…", ExportAsync); controls.SetFlowBreak(controls.Controls[^1], true);
        Button(controls, "Pause recording", () => ConfigureAsync(false)); Button(controls, "Resume recording", () => ConfigureAsync(true));
        controls.Controls.Add(new Label { Text = "Retain days", AutoSize = true }); controls.Controls.Add(days); controls.Controls.Add(new Label { Text = "Max MiB", AutoSize = true }); controls.Controls.Add(maxMiB);
        Button(controls, "Save retention", () => ConfigureAsync(config?.Settings.Enabled ?? true)); controls.Controls.Add(recording);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(series); split.Panel1.Controls.Add(new Label { Text = "SERIES  •  common value axis\nBOOL = 0/1 step trace", Dock = DockStyle.Top, Height = 52, Padding = new(6) });
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        right.Panel1.Controls.Add(plot); right.Panel2.Controls.Add(events); right.Panel2.Controls.Add(new Label { Text = "GAPS & QUALITY  •  shaded areas are missing, stale or interrupted data", Dock = DockStyle.Top, Height = 30, Padding = new(4) });
        split.Panel2.Controls.Add(right); Controls.Add(split); Controls.Add(status); Controls.Add(controls); Controls.Add(header);
        series.ItemCheck += (_, _) => { if (IsHandleCreated) BeginInvoke(() => UpdatePlot()); };
        days.ValueChanged += (_, _) => editingRetention = true; maxMiB.ValueChanged += (_, _) => editingRetention = true;
        from.Value = DateTime.Now.AddMinutes(-15); to.Value = DateTime.Now;
        follow.CheckedChanged += (_, _) => { from.Enabled = to.Enabled = !follow.Checked; }; from.Enabled = to.Enabled = false;
        timer.Tick += async (_, _) => { if (follow.Checked) await RunAsync(RefreshAsync); };
        Shown += async (_, _) => { split.SplitterDistance = 260; right.SplitterDistance = Math.Max(300, right.Height * 3 / 4); if (!fixture) { await RunAsync(RefreshAsync); timer.Start(); } };
        FormClosed += (_, _) => timer.Dispose();
    }
    private void Button(Control parent, string text, Func<Task> action)
    { var b = new Button { Text = text, AutoSize = true }; b.Click += async (_, _) => await RunAsync(action); parent.Controls.Add(b); }
    private async Task RunAsync(Func<Task> action)
    { if (busy) return; busy = true; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; } }
    private WatchRecordingQuery Query()
    {
        if (follow.Checked) { to.Value = DateTime.Now; from.Value = to.Value.AddMinutes(-(double)minutes.Value); }
        return new((int)revision.Value, new DateTimeOffset(from.Value), new DateTimeOffset(to.Value), Limit: 5000);
    }
    private async Task RefreshAsync()
    {
        var query = Query(); var page = await client.PostAsync<WatchRecordingPage>($"/api/watches/{watch.Definition.Id}/recording/query", query);
        config = page.Recording;
        if (!editingRetention) { days.Value = config.Settings.RetentionDays; maxMiB.Value = config.Settings.MaxMiB; editingRetention = false; }
        recording.Text = $"{(config.Settings.Enabled ? "Recording" : "Paused")} • {config.Bytes / 1048576.0:F1} MiB";
        trend = WatchTrends.Build(page.Records, page.Definition?.Settings.IntervalMs ?? watch.Definition.Settings.IntervalMs, query.From!.Value, query.To!.Value, page.Stale);
        RenderTrend(query.From.Value, query.To.Value);
        status.Text = $"{page.State} • {(page.Stale ? "STALE" : "current")} • {watch.Definition.Settings.Mode} • {page.Records.Count(r => r.Snapshot is not null)} recorded samples • local time axis\n" +
            (page.HasMore ? "Display limit reached: narrow the time range to see the remaining records. CSV exports the entire selected range. " : "") +
            (trend.SeriesLimitReached ? "Series limit reached: watch selected members to reduce the data. " : "") + (config.RecoveryNote ?? "");
        status.ForeColor = page.Stale ? Color.FromArgb(145, 77, 0) : Color.DarkGreen;
    }
    private void RenderTrend(DateTimeOffset start, DateTimeOffset end)
    {
        var checkedKeys = series.CheckedItems.Cast<string>().ToHashSet(StringComparer.Ordinal); var first = series.Items.Count == 0;
        series.BeginUpdate(); series.Items.Clear();
        foreach (var item in trend!.Series) series.Items.Add(item.Key, first ? series.Items.Count < 6 : checkedKeys.Contains(item.Key));
        series.EndUpdate(); plot.From = start; plot.To = end; plot.Gaps = trend.Gaps;
        events.DataSource = trend.Gaps.Select(g => new { From = g.From.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), To = g.To.ToLocalTime().ToString("HH:mm:ss.fff"), g.Reason }).ToArray();
        UpdatePlot();
    }
    private void UpdatePlot()
    { plot.Series = trend?.Series.Where(s => series.CheckedItems.Contains(s.Key)).ToArray() ?? []; plot.Invalidate(); }
    private async Task ConfigureAsync(bool enabled)
    {
        config ??= await client.GetAsync<WatchRecordingStatus>($"/api/watches/{watch.Definition.Id}/recording");
        config = await client.PostAsync<WatchRecordingStatus>($"/api/watches/{watch.Definition.Id}/recording", new WatchRecordingUpdate(new(enabled, (int)days.Value, (int)maxMiB.Value), config.Revision));
        editingRetention = false; await RefreshAsync();
    }
    private async Task ExportAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "Watch recording CSV|*.csv", FileName = "watch-recording.csv", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (File.Exists(dialog.FileName)) throw new ArgumentException("Choose a new filename; existing files are preserved.");
        var artifact = await client.PostAsync<Artifact>($"/api/watches/{watch.Definition.Id}/recording/export", Query());
        var bytes = await client.DownloadAsync(artifact.Id); if (ProjectService.Hash(bytes) != artifact.Sha256) throw new IOException("CSV artifact hash mismatch.");
        await using var file = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write); await file.WriteAsync(bytes);
        status.Text = $"CSV saved: {dialog.FileName}. UTC timestamps, exact JSON values, quality and session events included.";
    }
    public void LoadForTest(WatchRecord[] records)
    {
        fixture = true; follow.Checked = false;
        var start = records.First().At.AddSeconds(-2); var end = records.Last().At.AddSeconds(4);
        trend = WatchTrends.Build(records, 1000, start, end, true); RenderTrend(start, end);
        recording.Text = "Recording • 0.4 MiB / 64 MiB";
        status.Text = "stopped • STALE • Offline fixture • communication interruption shaded; lines do not cross the gap.\nExact values remain available in CSV; this graph uses a common numeric axis.";
    }
}

internal sealed class TrendPlot : Control
{
    public TrendSeries[] Series { get; set; } = [];
    public TrendGap[] Gaps { get; set; } = [];
    public DateTimeOffset From { get; set; } = DateTimeOffset.Now.AddMinutes(-15);
    public DateTimeOffset To { get; set; } = DateTimeOffset.Now;
    private static readonly Color[] Palette = [Color.FromArgb(0, 117, 153), Color.FromArgb(221, 100, 35), Color.FromArgb(65, 143, 74), Color.FromArgb(135, 80, 165), Color.FromArgb(200, 65, 100), Color.FromArgb(150, 120, 25)];
    private readonly ToolTip tip = new();
    private readonly List<(PointF Point, string Text)> hits = [];
    private string? tipText;
    public TrendPlot()
    {
        DoubleBuffered = true; BackColor = Color.White; ResizeRedraw = true;
        MouseMove += (_, e) =>
        {
            var closest = hits.OrderBy(h => Math.Abs(h.Point.X - e.X) + Math.Abs(h.Point.Y - e.Y)).FirstOrDefault();
            var text = Math.Abs(closest.Point.X - e.X) + Math.Abs(closest.Point.Y - e.Y) < 20 ? closest.Text : null;
            if (text == tipText) return; tipText = text; tip.SetToolTip(this, text);
        };
    }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; hits.Clear();
        var box = new RectangleF(80, 46, Math.Max(20, Width - 105), Math.Max(20, Height - 105));
        var values = Series.SelectMany(s => s.Points).Where(p => p.Value is not null && p.At >= From && p.At <= To).Select(p => p.Value!.Value).ToArray();
        var min = values.Length == 0 ? 0 : values.Min(); var max = values.Length == 0 ? 1 : values.Max();
        if (max - min < 1e-9) { min -= .5; max += .5; } else { var padding = (max - min) * .08; min -= padding; max += padding; }
        float X(DateTimeOffset at) => box.Left + (float)((at - From).TotalSeconds / Math.Max(.001, (To - From).TotalSeconds)) * box.Width;
        float Y(double value) => box.Bottom - (float)((value - min) / (max - min)) * box.Height;
        using var grid = new Pen(Color.FromArgb(220, 227, 233)); using var label = new SolidBrush(Color.FromArgb(60, 76, 92));
        g.DrawString("Value  •  hover a point for its exact recorded JSON value", Font, label, 12, 12);
        g.SetClip(box);
        using var gapBrush = new SolidBrush(Color.FromArgb(36, 195, 135, 60));
        foreach (var gap in Gaps) g.FillRectangle(gapBrush, X(gap.From), box.Top, Math.Max(2, X(gap.To) - X(gap.From)), box.Height);
        g.ResetClip();
        for (var i = 0; i <= 5; i++)
        {
            var y = box.Bottom - box.Height * i / 5; g.DrawLine(grid, box.Left, y, box.Right, y);
            g.DrawString((min + (max - min) * i / 5).ToString("G5"), Font, label, 4, y - 9);
            var at = From + TimeSpan.FromTicks((To - From).Ticks * i / 5); var x = X(at); g.DrawLine(grid, x, box.Top, x, box.Bottom);
            g.DrawString(at.ToLocalTime().ToString("HH:mm:ss"), Font, label, x - 30, box.Bottom + 8);
        }
        g.SetClip(box);
        for (var i = 0; i < Series.Length; i++)
        {
            var series = Series[i]; using var pen = new Pen(Palette[i % Palette.Length], 2); using var brush = new SolidBrush(pen.Color); PointF? previous = null;
            foreach (var point in series.Points)
            {
                if (point.Value is null || point.At < From || point.At > To) { previous = null; continue; }
                var current = new PointF(X(point.At), Y(point.Value.Value));
                if (previous is { } before)
                {
                    if (series.Boolean) { g.DrawLine(pen, before, new PointF(current.X, before.Y)); g.DrawLine(pen, new PointF(current.X, before.Y), current); }
                    else g.DrawLine(pen, before, current);
                }
                g.FillEllipse(brush, current.X - 2, current.Y - 2, 4, 4); previous = current;
                hits.Add((current, $"{series.Key}\n{point.At.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}\n{point.ExactValue} • {point.Quality}"));
            }
        }
        g.ResetClip();
        if (values.Length == 0) g.DrawString("No numeric/BOOL samples in this range. Select series or change the time range.", Font, label, box.Left, Height - 27);
        else
        {
            var left = box.Left;
            for (var i = 0; i < Series.Length && left < box.Right - 90; i++)
            {
                using var ink = new SolidBrush(Palette[i % Palette.Length]); var text = "● " + Series[i].Key + "   ";
                g.DrawString(text, Font, ink, left, Height - 27); left += g.MeasureString(text, Font).Width;
            }
        }
    }
}
