using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class TagValueMergeForm : Form
{
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly ComboBox source = Choice(320), destination = Choice(320), sourceScope = Choice(220), destinationScope = Choice(220), target = Choice(190);
    private readonly TextBox jobId = new() { Width = 260, PlaceholderText = "Completed upload job ID" };
    private readonly DataGridView rows = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
    private readonly TextBox detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 54, Padding = new(8), Text = "Open two different projects. Find tags in matching scopes, select rows and preview the selected merge." };
    private readonly Button apply = new() { Text = "Apply reviewed offline merge", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private TagValueMergeRequest? reviewed;
    private TagValueMergeReport? report;
    private string? captureJob;
    private bool busy, suppress, fixture;
    private sealed record Scope(string? Name) { public override string ToString() => Name ?? "(Controller scope)"; }
    private static ComboBox Choice(int width) => new() { Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
    public TagValueMergeForm(ServiceClient client, ProjectInfo? initial)
    {
        this.client = client; this.initial = initial;
        Text = "Merge PLC tag values into an offline project"; Size = new(1500, 960); MinimumSize = new(1150, 780); Font = new("Segoe UI", 10);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 175, Padding = new(8), AutoScroll = true };
        Label(top, "Source snapshot"); top.Controls.Add(source); Button(top, "Open source…", () => OpenAsync(source)); Label(top, "Source scope"); top.Controls.Add(sourceScope); Break(top);
        Label(top, "Offline destination"); top.Controls.Add(destination); Button(top, "Open destination…", () => OpenAsync(destination)); Label(top, "Destination scope"); top.Controls.Add(destinationScope); Button(top, "Refresh snapshots", InitializeAsync); Break(top);
        Label(top, "Online source target"); top.Controls.Add(target); Button(top, "Upload online source", CaptureAsync); Button(top, "Cancel upload", CancelCaptureAsync); top.Controls.Add(jobId); Button(top, "Load upload job", LoadJobAsync); Break(top);
        top.Controls.Add(new Label { AutoSize = true, Text = "Upload reads the selected configured controller into a saved snapshot. Merge and export affect the offline destination; no controller values are written." });
        source.SelectedIndexChanged += async (_, _) => { if (!suppress) await Run(() => ScopesAsync(source, sourceScope)); };
        destination.SelectedIndexChanged += async (_, _) => { if (!suppress) await Run(() => ScopesAsync(destination, destinationScope)); };
        sourceScope.SelectedIndexChanged += (_, _) => InvalidateReview(); destinationScope.SelectedIndexChanged += (_, _) => InvalidateReview();
        rows.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "Merge", FillWeight = 35 });
        foreach (var name in new[] { "Source tag", "Destination tag", "Data type", "Status", "Before", "After", "Diagnostic" }) rows.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, ReadOnly = true });
        rows.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        rows.CurrentCellDirtyStateChanged += (_, _) => { if (rows.IsCurrentCellDirty) rows.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        rows.CellValueChanged += (_, _) => { if (!suppress) InvalidateReview(); };
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal }; split.Panel1.Controls.Add(rows); split.Panel2.Controls.Add(detail);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new(8) };
        Button(actions, "Find matching tags", FindAsync); Button(actions, "Preview selected values", PreviewAsync); actions.Controls.Add(apply); apply.Click += async (_, _) => await Run(ApplyAsync);
        Button(actions, "Export destination L5X…", () => ExportAsync("l5x")); Button(actions, "Export destination ACD…", () => ExportAsync("acd"));
        Controls.Add(split); Controls.Add(actions); Controls.Add(status); Controls.Add(top);
        Shown += async (_, _) => { split.SplitterDistance = Math.Max(220, split.Height * 3 / 5); if (!fixture) await Run(InitializeAsync); };
        timer.Tick += async (_, _) => { if (!busy && captureJob is not null) await Run(PollCaptureAsync); };
        FormClosed += (_, _) => timer.Dispose();
    }
    private static void Label(Control parent, string text) => parent.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new(0, 7, 0, 0) });
    private static void Break(FlowLayoutPanel parent) => parent.SetFlowBreak(parent.Controls[^1], true);
    private void Button(Control parent, string text, Func<Task> action)
    { var b = new Button { Text = text, AutoSize = true }; parent.Controls.Add(b); b.Click += async (_, _) => await Run(action); }
    private async Task Run(Func<Task> action)
    {
        if (busy || fixture) return; busy = true; UseWaitCursor = true; Enabled = false;
        try { await action(); } catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; UseWaitCursor = false; Enabled = true; }
    }
    private void InvalidateReview() { reviewed = null; report = null; apply.Enabled = false; detail.Text = "Selection changed. Preview selected values again before applying."; }
    private async Task InitializeAsync()
    {
        var from = (source.SelectedItem as ReviewProjectChoice)?.Project.Id; var to = (destination.SelectedItem as ReviewProjectChoice)?.Project.Id ?? initial?.Id;
        var choices = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").Select(p => new ReviewProjectChoice(p)).ToArray();
        suppress = true; source.DataSource = choices; destination.DataSource = choices.ToArray();
        source.SelectedItem = choices.FirstOrDefault(c => c.Project.Id == from) ?? choices.FirstOrDefault(c => c.Project.Id != to) ?? choices.FirstOrDefault();
        destination.SelectedItem = choices.FirstOrDefault(c => c.Project.Id == to) ?? choices.LastOrDefault(); suppress = false;
        await ScopesAsync(source, sourceScope); await ScopesAsync(destination, destinationScope);
        var caps = await client.GetAsync<JsonElement>("/api/sdk/capabilities"); target.DataSource = caps.GetProperty("controllerTargets").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToArray();
    }
    private async Task ScopesAsync(ComboBox choice, ComboBox scopes)
    {
        var previous = (scopes.SelectedItem as Scope)?.Name; var items = new List<Scope> { new(null) };
        if (choice.SelectedItem is ReviewProjectChoice p)
        {
            for (var offset = 0; ; offset += 1000)
            {
                var page = await client.GetAsync<Inspection>($"/api/projects/{p.Project.Id}/elements?kind=Program&offset={offset}&limit=1000");
                items.AddRange(page.Elements.Select(e => new Scope(e.Name))); if (offset + page.Elements.Count >= page.Total) break;
            }
        }
        scopes.DataSource = items; scopes.SelectedItem = items.FirstOrDefault(s => s.Name == previous) ?? items[0]; rows.Rows.Clear(); InvalidateReview();
    }
    private async Task OpenAsync(ComboBox choice)
    {
        using var dialog = new OpenFileDialog { Filter = "Logix projects|*.acd;*.l5x" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(dialog.FileName)); await InitializeAsync();
        choice.SelectedItem = choice.Items.Cast<ReviewProjectChoice>().Single(c => c.Project.Id == project.Id); await ScopesAsync(choice, choice == source ? sourceScope : destinationScope);
    }
    private TagValueMergeRequest Request(TagValueMap[]? maps = null)
    {
        if (source.SelectedItem is not ReviewProjectChoice a || destination.SelectedItem is not ReviewProjectChoice b) throw new ArgumentException("Open/select two different Logix snapshots.");
        return new(a.Project.Id, a.Project.CurrentSha256, b.Project.Id, b.Project.CurrentSha256, maps, (sourceScope.SelectedItem as Scope)?.Name, (destinationScope.SelectedItem as Scope)?.Name);
    }
    private async Task FindAsync() { var request = Request(); Show(await client.PostAsync<TagValueMergeReport>("/api/value-merge/preview", request), null); }
    private async Task PreviewAsync()
    {
        rows.EndEdit(); var maps = rows.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true).Select(r => (TagValueMap)r.Tag!).ToArray();
        if (maps.Length == 0) throw new ArgumentException("Select at least one tag row.");
        var request = Request(maps); Show(await client.PostAsync<TagValueMergeReport>("/api/value-merge/preview", request), request);
    }
    private void Show(TagValueMergeReport value, TagValueMergeRequest? request)
    {
        suppress = true; rows.Rows.Clear();
        foreach (var item in value.Rows)
        {
            string Path(string? program, string tag) => (program ?? "Controller") + "/" + tag;
            var row = rows.Rows[rows.Rows.Add(item.Status == "ready", Path(item.Tag.SourceProgram, item.Tag.SourceTag), Path(item.Tag.DestinationProgram, item.Tag.DestinationTag), item.DataType, item.Status, item.Before, item.After, item.Message)]; row.Tag = item.Tag;
            if (item.Status is not ("ready" or "unchanged")) row.DefaultCellStyle.BackColor = Color.MistyRose;
        }
        suppress = false; reviewed = request; report = value; apply.Enabled = request is not null && value.CanApply;
        detail.Text = $"Origin: {value.Origin} · captured {value.CapturedAt?.ToString("O") ?? "saved values"}\r\nSource SHA-256: {value.Source.CurrentSha256}\r\nDestination SHA-256: {value.Destination.CurrentSha256}\r\nReview SHA-256: {value.ReviewSha256}\r\nResult SHA-256: {value.ResultSha256}\r\n\r\n{value.Coverage}";
        status.Text = $"{value.Rows.Length} rows · {value.Changed} changed representations. " + (request is null ? "Select compatible rows, then Preview selected values." : value.CanApply ? "Review before/after values, then apply to the offline destination." : "Resolve/remove incompatible rows and preview again.");
    }
    private async Task ApplyAsync()
    {
        if (reviewed is null || report is not { CanApply: true }) throw new ArgumentException("Preview the selected values first.");
        var result = await client.PostAsync<ProjectInfo>("/api/value-merge/apply", new TagValueMergeApplyRequest(reviewed, report.ReviewSha256));
        await InitializeAsync(); status.Text = $"Offline destination revision {result.Revision} saved. Export a separate L5X/ACD for native validation.";
    }
    private async Task ExportAsync(string format)
    {
        if (destination.SelectedItem is not ReviewProjectChoice p) throw new ArgumentException("Select an offline destination.");
        using var dialog = new SaveFileDialog { Filter = format.ToUpperInvariant() + "|*." + format, FileName = p.Project.Name + "-merged." + format, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var artifact = await client.PostAsync<Artifact>($"/api/projects/{p.Project.Id}/exports", new ExportRequest(p.Project.CurrentSha256, format));
        var saved = await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(dialog.FileName)); status.Text = "Saved new offline file: " + saved.Path;
    }
    private async Task CaptureAsync()
    {
        if (captureJob is not null) throw new ArgumentException("Wait for the current upload job; it is never retried automatically.");
        if (target.SelectedItem is not string id) throw new ArgumentException("Select an enabled configured controller target first.");
        var job = await client.PostAsync<SdkJob>("/api/value-merge/capture", new StructurePullRequest(id)); captureJob = job.Id; jobId.Text = job.Id; timer.Start(); status.Text = "Online source upload queued: " + job.Id;
    }
    private async Task CancelCaptureAsync() { if (captureJob is not null) { await client.PostAsync<SdkJob>($"/api/sdk/jobs/{captureJob}/cancel", new { }); status.Text = "Cancellation requested; waiting for the terminal job status."; } }
    private async Task PollCaptureAsync()
    {
        var job = await client.GetAsync<SdkJob>("/api/sdk/jobs/" + captureJob);
        if (job.Status is "queued" or "running") { status.Text = "Online source upload " + job.Status + ": " + job.Id; return; }
        timer.Stop(); captureJob = null; if (job.Status != "succeeded") { status.Text = job.Status + ": " + job.Error; return; } await LoadJobAsync();
    }
    private async Task LoadJobAsync()
    {
        var job = await client.GetAsync<SdkJob>("/api/sdk/jobs/" + Uri.EscapeDataString(jobId.Text.Trim()));
        if (job.Status != "succeeded" || job.ResultProject is null || !job.Methods.SequenceEqual(new[] { "UploadToNewProjectAsync" })) throw new ArgumentException("Choose a successful upload-only job with a published project.");
        await InitializeAsync(); source.SelectedItem = source.Items.Cast<ReviewProjectChoice>().Single(c => c.Project.Id == job.ResultProject.Id); await ScopesAsync(source, sourceScope);
        status.Text = "Uploaded snapshot loaded. Choose scopes and tag values; capture does not apply a merge.";
    }
    public void LoadForTest()
    {
        fixture = true; var a = new ProjectInfo("source", "Saved source example", "Source.L5X", "source-hash", "source-hash", 0, DateTimeOffset.UnixEpoch);
        var b = a with { Id = "destination", Name = "Offline destination", CurrentSha256 = "destination-hash" };
        suppress = true; source.Items.Add(new ReviewProjectChoice(a)); destination.Items.Add(new ReviewProjectChoice(b)); source.SelectedIndex = destination.SelectedIndex = 0;
        sourceScope.Items.Add(new Scope("Example")); destinationScope.Items.Add(new Scope("Replacement")); sourceScope.SelectedIndex = destinationScope.SelectedIndex = 0; suppress = false;
        var request = new TagValueMergeRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256);
        Show(new(a, b, "saved_project", null, "generated-review-hash", "generated-result-hash", true, 2,
            [new(new("Recipe", "Example", "Recipe", "Replacement"), "RecipeUDT", "ready", "{\"Count\":3,\"Setpoints\":[1,2,3]}", "{\"Count\":7,\"Setpoints\":[10,20,30]}", null),
             new(new("Enabled", "Example", "Enabled", "Replacement"), "BOOL", "ready", "false", "true", null)],
            "Generated fixture. Whole-tag values only; reviewed offline mutation. Original projects and ladder logic remain preserved. Native and live validation are separate."), request); apply.Enabled = false;
    }
}
