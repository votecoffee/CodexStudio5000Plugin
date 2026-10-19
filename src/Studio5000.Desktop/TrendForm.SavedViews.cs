using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed partial class TrendForm
{
    private readonly ComboBox savedViews = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly TextBox viewName = new() { Width = 220, PlaceholderText = "Saved trend name", Text = "Process trend" };
    private readonly DataGridView statistics = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = false, RowHeadersVisible = false };
    private SavedTrend? loadedView;
    private TrendAnalysis? analysis;
    private TrendAnalyzeRequest? analysisRequest;
    private Control SavedTrendControls()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new(8), BackColor = Color.FromArgb(230, 237, 245) };
        bar.Controls.Add(new Label { Text = "Saved trend", AutoSize = true, Padding = new(0, 7, 0, 0) }); bar.Controls.Add(savedViews);
        Button(bar, "Refresh views", RefreshViewsAsync); Button(bar, "Load", LoadViewAsync); bar.Controls.Add(viewName);
        Button(bar, "Save new", () => SaveViewAsync(false)); Button(bar, "Update loaded", () => SaveViewAsync(true)); bar.SetFlowBreak(bar.Controls[^1], true);
        Button(bar, "Delete loaded", DeleteViewAsync); Button(bar, "Gather results", () => AnalyzeViewAsync(false)); Button(bar, "Next result page", () => AnalyzeViewAsync(true)); Button(bar, "Export results JSON…", ExportResultsAsync);
        Button(bar, "Diagnostic triggers…", () => { using var diagnostics = new DiagnosticsForm(client, initialWatch: watch); diagnostics.ShowDialog(this); return Task.CompletedTask; });
        return bar;
    }
    private async Task RefreshViewsAsync()
    {
        var id = (savedViews.SelectedItem as SavedTrendSummary)?.Id ?? loadedView?.Id;
        var views = (await client.GetAsync<SavedTrendSummary[]>("/api/trends")).Where(t => t.WatchId == watch.Definition.Id).ToArray(); savedViews.DataSource = views;
        savedViews.SelectedItem = views.FirstOrDefault(t => t.Id == id) ?? views.FirstOrDefault();
    }
    private async Task LoadViewAsync()
    {
        if (savedViews.SelectedItem is not SavedTrendSummary selected) throw new InvalidOperationException("Select a saved trend view.");
        loadedView = await client.GetAsync<SavedTrend>("/api/trends/" + selected.Id);
        revision.Value = loadedView.WatchRevision; minutes.Value = loadedView.WindowMinutes; viewName.Text = loadedView.Name;
        await RefreshAsync(); SelectViewSeries();
        status.Text += $"\nLoaded {loadedView.Name} • view r{loadedView.Revision} • source {loadedView.ProjectSha256[..12]}";
    }
    private void SelectViewSeries()
    {
        for (var i = 0; i < series.Items.Count; i++) { var key = (string)series.Items[i]; series.SetItemChecked(i, loadedView!.Series.Any(s => key == s || key.StartsWith(s + ".", StringComparison.Ordinal) || key.StartsWith(s + "[", StringComparison.Ordinal))); }
    }
    private async Task SaveViewAsync(bool update)
    {
        if (update && loadedView is null) throw new InvalidOperationException("Load a saved view before updating it.");
        var selected = series.CheckedItems.Cast<string>().ToArray();
        if (selected.Length == 0) selected = loadedView?.Series ?? watch.Definition.Settings.Tags.Select(t => t.Key).ToArray();
        loadedView = await client.PostAsync<SavedTrend>("/api/trends", new SavedTrendRequest(viewName.Text, watch.Definition.Id, (int)revision.Value, selected, (int)minutes.Value, update ? loadedView!.Id : null, update ? loadedView!.Revision : null));
        analysis = null; analysisRequest = null; await RefreshViewsAsync();
        status.Text = $"Saved {loadedView.Name} • r{loadedView.Revision}. Selected series and recorded watch r{loadedView.WatchRevision} are pinned. Polling state is unchanged.";
    }
    private async Task DeleteViewAsync()
    {
        if (loadedView is null) throw new InvalidOperationException("Load a saved view before deleting its definition.");
        await client.PostAsync<object>("/api/trends/" + loadedView.Id + "/delete", new ReviewDeleteRequest(loadedView.Revision)); loadedView = null; analysis = null; analysisRequest = null; await RefreshViewsAsync(); status.Text = "Saved view removed. Watch list, recordings and exported results are preserved.";
    }
    private async Task AnalyzeViewAsync(bool next)
    {
        if (loadedView is null) throw new InvalidOperationException("Save or load a trend view before gathering results.");
        TrendAnalyzeRequest request;
        if (next)
        {
            if (analysis is not { HasMore: true } || analysisRequest is null) throw new InvalidOperationException("The displayed result has no next page.");
            request = analysisRequest with { AfterIndex = analysis.NextIndex };
        }
        else { var q = Query(); request = new(loadedView.Id, loadedView.Revision, q.From, q.To); }
        var result = await client.PostAsync<TrendAnalysis>("/api/trends/analyze", request);
        follow.Checked = false; from.Value = result.From.LocalDateTime; to.Value = result.To.LocalDateTime;
        analysis = result; analysisRequest = request; trend = result.Trend; RenderTrend(result.From, result.To); SelectViewSeries(); statistics.DataSource = result.Statistics;
        status.Text = $"Gathered {result.Statistics.Length} series • view r{result.Definition.Revision} • watch r{result.Definition.WatchRevision} • result {result.ResultSha256[..12]}\n" +
            (result.HasMore ? $"PARTIAL PAGE: statistics cover this page only. Next index {result.NextIndex}; use Next result page or narrow the range." : result.Trend.SeriesLimitReached ? "Series/traversal limit reached. Select fewer scalar members; results are incomplete." : "Complete returned range. Gaps/bad quality remain visible. Exact first/last values accompany numeric statistics.");
    }
    private async Task<Artifact> ExportResultArtifactAsync()
    {
        if (analysis is null || analysisRequest is null) throw new InvalidOperationException("Gather results first. Refreshing the graph invalidates the previous export selection.");
        return await client.PostAsync<Artifact>("/api/trends/export", new TrendExportRequest(analysisRequest, analysis.ResultSha256));
    }
    private async Task ExportResultsAsync()
    {
        if (analysis is null) throw new InvalidOperationException("Gather results before exporting.");
        using var dialog = new SaveFileDialog { Filter = "Trend analysis JSON|*.json", FileName = "trend-results.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var artifact = await ExportResultArtifactAsync(); var saved = await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(dialog.FileName));
        status.Text = "Results saved with source/view revision, exact values, statistics and gaps: " + saved.Path;
    }
    internal async Task<string[]> ExerciseSavedViewsForTestAsync(string root)
    {
        fixture = true; await RefreshViewsAsync(); viewName.Text = "Generated desktop trend"; await SaveViewAsync(false); var id = loadedView!.Id;
        viewName.Text += " updated"; await SaveViewAsync(true); if (loadedView.Revision != 1) throw new InvalidOperationException("View update did not advance revision.");
        await LoadViewAsync(); await AnalyzeViewAsync(false); if (analysis!.Definition.Id != id || analysis.Definition.Revision != 1) throw new InvalidOperationException("Analysis lost its loaded revision.");
        var artifact = await ExportResultArtifactAsync(); var saved = await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(Path.Combine(root, "artifacts", "exports", "desktop-trend-" + id + ".json")));
        if (ProjectService.Hash(await File.ReadAllBytesAsync(saved.Path)) != artifact.Sha256) throw new InvalidOperationException("Trend results hash mismatch.");
        await DeleteViewAsync(); if ((await client.GetAsync<WatchStatus>("/api/watches/" + watch.Definition.Id)).State != "stopped") throw new InvalidOperationException("Trend workflow changed polling state.");
        return ["Create and update saved trend", "Load exact view/watch revision", "Gather stopped recording without polling", "Export verified result artifact", "Delete view preserving stopped watch"];
    }
}
