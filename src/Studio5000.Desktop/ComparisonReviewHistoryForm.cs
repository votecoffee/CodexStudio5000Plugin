using System.Xml.Linq;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class ComparisonReviewHistoryForm : WorkspaceForm
{
    private sealed record Row(ComparisonReviewEntry Entry)
    {
        public string Created => Entry.CreatedAt.ToLocalTime().ToString("g");
        public string Left => Entry.Left.Name + " · r" + Entry.Left.Revision;
        public string Right => Entry.Right.Name + " · r" + Entry.Right.Revision;
        public string Format => Entry.Format;
        public string Changes => Entry.IncludedChanges + " / " + Entry.TotalChanges;
    }
    private readonly ServiceClient client;
    private readonly TextBox query = new() { Width = 310, PlaceholderText = "Search project names, snapshot hashes or report IDs" };
    private readonly DataGridView reviews = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 56, Padding = new(8), Text = "Reports retain recorded hashes even when managed projects change. Save a separate report copy for review." };
    private ComparisonReviewPage? page;
    private bool busy, fixture;
    public ComparisonReviewEntry? Selection { get; private set; }
    public ComparisonReviewHistoryForm(ServiceClient client)
    {
        this.client = client; Text = "Comparison report history"; Size = new(1200, 780); MinimumSize = new(960, 650); Font = new("Segoe UI", 10);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(8) }; top.Controls.Add(query);
        Button(top, "Find / refresh", () => LoadAsync()); Button(top, "Previous", () => PageAsync(-1)); Button(top, "Next", () => PageAsync(1));
        foreach (var (property, label, width) in new[] { ("Created", "Created locally", 165), ("Left", "Left snapshot", 310), ("Right", "Right snapshot", 310), ("Format", "Format", 75), ("Changes", "Included / total", 125) })
            reviews.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = label, Width = width });
        reviews.SelectionChanged += (_, _) => ShowDetails();
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal }; split.Panel1.Controls.Add(reviews); split.Panel2.Controls.Add(details);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(8) };
        Button(actions, "Save report copy…", SaveAsync); Button(actions, "Review changes / notes…", AnnotationsAsync); Button(actions, "Compare recorded snapshots", UseAsync); Button(actions, "Forget history entry", ForgetAsync);
        Controls.Add(split); Controls.Add(actions); Controls.Add(top); Controls.Add(status);
        Shown += async (_, _) => { split.SplitterDistance = Math.Max(100, split.Height / 2); if (!fixture) await Run(() => LoadAsync()); };
    }
    private void Button(Control parent, string text, Func<Task> action)
    { var button = new Button { Text = text, AutoSize = true }; parent.Controls.Add(button); button.Click += async (_, _) => await Run(action); }
    private async Task Run(Func<Task> action)
    { if (busy || fixture) return; busy = true; Enabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; Enabled = true; } }
    private ComparisonReviewEntry? Selected() => (reviews.CurrentRow?.DataBoundItem as Row)?.Entry;
    private void ShowDetails()
    {
        var entry = Selected(); if (entry is null) { details.Text = "Select a recorded report."; return; }
        static string Option(bool enabled) => enabled ? "included" : "omitted";
        details.Text = string.Join(Environment.NewLine,
            "LEFT SNAPSHOT", $"{entry.Left.Name} · revision {entry.Left.Revision}", "SHA-256: " + entry.Left.Sha256, "",
            "RIGHT SNAPSHOT", $"{entry.Right.Name} · revision {entry.Right.Revision}", "SHA-256: " + entry.Right.Sha256, "",
            "RECORDED COMPARISON", $"{entry.IncludedChanges} matching changes / {entry.TotalChanges} total changes", "Filter: " + (entry.Query ?? "(all changes)"),
            "Rung numbers: " + Option(entry.IncludeRungNumbers), "XML snippets: " + Option(entry.IncludeXml), "Ladder drawings: " + Option(entry.IncludeLadder), "",
            "REPORT FILE", $"{entry.Format.ToUpperInvariant()} · {entry.Bytes:N0} bytes · {entry.CreatedAt.ToLocalTime():g}", "SHA-256: " + entry.Sha256, "",
            "Structural saved-project review only. No live power flow, simulation or native acceptance.",
            "Save a report copy even after project changes. Reopening a comparison requires unchanged recorded snapshots.");
    }
    private void ShowPage(ComparisonReviewPage value)
    {
        page = value; reviews.DataSource = value.Reviews.Select(e => new Row(e)).ToArray();
        status.Text = $"{value.Total} matching reports; {value.Reviews.Length} shown at offset {value.Offset}. Forget removes the history entry and preserves its artifact.";
    }
    private async Task LoadAsync(int offset = 0) => ShowPage(await client.GetAsync<ComparisonReviewPage>("/api/comparison-reviews?limit=50&offset=" + offset + "&query=" + Uri.EscapeDataString(query.Text.Trim())));
    private async Task PageAsync(int direction)
    { if (page is null || direction > 0 && page.Offset + page.Reviews.Length >= page.Total) return; await LoadAsync(Math.Max(0, page.Offset + direction * page.Limit)); }
    private async Task<ComparisonReviewEntry> VerifiedAsync()
    {
        var selected = Selected() ?? throw new InvalidOperationException("Select a recorded report.");
        var current = await client.GetAsync<ComparisonReviewEntry>("/api/comparison-reviews/" + selected.Id);
        if (current.Sha256 != selected.Sha256) throw new InvalidOperationException("Report changed; refresh history.");
        return current;
    }
    private async Task SaveAsync()
    {
        var entry = await VerifiedAsync(); using var dialog = new SaveFileDialog { Filter = $"Comparison report|*.{entry.Format}", FileName = "comparison-review." + entry.Format, OverwritePrompt = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var result = await SaveFileAsync(dialog.FileName); status.Text = "Saved verified report copy: " + result.Path;
    }
    private async Task<SavedArtifact> SaveFileAsync(string path)
    { var entry = await VerifiedAsync(); return await client.PostAsync<SavedArtifact>($"/api/artifacts/{entry.ArtifactId}/save", new SaveArtifactRequest(path)); }
    private async Task UseAsync() { Selection = await VerifiedAsync(); DialogResult = DialogResult.OK; Close(); }
    private async Task AnnotationsAsync()
    { var selected = await VerifiedAsync(); using var form = new ComparisonAnnotationsForm(client, selected); form.ShowDialog(this); }
    private async Task ForgetAsync()
    {
        var entry = Selected() ?? throw new InvalidOperationException("Select a recorded report.");
        await client.PostAsync<object>($"/api/comparison-reviews/{entry.Id}/forget", new ComparisonReviewDeleteRequest(entry.Sha256));
        await LoadAsync(); status.Text = "History entry forgotten. Its immutable artifact, exported files and project snapshots are preserved.";
    }
    public void LoadForTest(bool workflow = false)
    {
        fixture = true; if (workflow) return;
        var a = new ComparisonSnapshot(new string('a', 32), "Baseline example", 0, new string('a', 64)); var b = new ComparisonSnapshot(new string('b', 32), "Edited example", 2, new string('b', 64));
        ShowPage(new(1, 0, 50, [new(new string('c', 32), DateTimeOffset.UnixEpoch, "html", new string('c', 32), new string('d', 64), 12345, a, b, "Rung", false, false, true, 15, 8)]));
    }
    public string[] ExerciseOfflineWorkflowForTest(string root)
    {
        if (!fixture) throw new InvalidOperationException("Generated off-screen validation only.");
        var checks = new List<string>(); void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        async Task ExerciseAsync()
        {
            var doc = XDocument.Parse("<RSLogix5000Content><Controller Name='Generated'><Tags><Tag Name='Count' DataType='DINT'><DataValue Value='1'/></Tag></Tags></Controller></RSLogix5000Content>");
            var aPath = Path.Combine(root, "examples", "GuiReviewHistoryLeft.l5x"); var bPath = Path.Combine(root, "examples", "GuiReviewHistoryRight.l5x"); doc.Save(aPath);
            doc.Descendants("DataValue").Single().SetAttributeValue("Value", "2"); doc.Save(bPath);
            var a = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(aPath)); var b = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(bPath));
            var exported = await client.PostAsync<ComparisonReviewExport>("/api/comparison-reviews/export", new ComparisonReviewExportRequest(new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256)));
            query.Text = exported.Review.Id; await LoadAsync(); Check(page!.Total == 1 && Selected()?.Id == exported.Review.Id, "GUI history searches immutable report entries through the shared service");
            var output = Path.Combine(root, "artifacts", "exports", "gui-history-review.html"); var copy = await SaveFileAsync(output);
            Check(copy.Sha256 == exported.Artifact.Sha256 && ProjectService.Hash(File.ReadAllBytes(output)) == copy.Sha256, "GUI history saves a separately verified report copy through the artifact service");
            var rejected = false; try { await SaveFileAsync(output); } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest) { rejected = true; }
            Check(rejected && File.Exists(output), "GUI report-copy action preserves existing output files");
            await ForgetAsync(); Check(page!.Total == 0 && (await client.DownloadAsync(exported.Artifact.Id)).Length == copy.Bytes, "GUI forgetting a history entry preserves its report artifact and saved copy");
            // Leave a report selected for the layout capture.
            exported = await client.PostAsync<ComparisonReviewExport>("/api/comparison-reviews/export", new ComparisonReviewExportRequest(new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256), "json"));
            query.Text = exported.Review.Id; await LoadAsync();
        }
        WorkflowValidation.Run(ExerciseAsync); return checks.ToArray();
    }
}
