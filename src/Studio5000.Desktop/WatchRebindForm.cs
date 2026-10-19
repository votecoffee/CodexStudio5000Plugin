using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class WatchRebindForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly WatchStatus watch;
    private readonly ProjectInfo? initial;
    private readonly ComboBox projects = new() { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", FormattingEnabled = true };
    private readonly DataGridView mappings = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly DataGridView changes = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
    private readonly TextBox detail = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly Button apply = new() { Text = "Apply stopped-list rebind", AutoSize = true, Enabled = false };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 58, Padding = new(8), Text = "Choose a destination and preview. Rebind preserves this list's settings and retained history, and leaves polling stopped." };
    private WatchRebindPreviewRequest? reviewed;
    private WatchRebindPreviewReport? preview;
    private bool busy, fixture, suppress;
    public WatchStatus? ReboundWatch { get; private set; }

    public WatchRebindForm(ServiceClient client, WatchStatus watch, ProjectInfo? initial)
    {
        this.client = client; this.watch = watch; this.initial = initial;
        Text = "Rebind watch source — " + watch.Definition.Settings.Name; Size = new(1400, 900); MinimumSize = new(1120, 740); Font = new("Segoe UI", 10);
        var settings = watch.Definition.Settings;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 100, Padding = new(8) };
        var context = new Label { Text = $"Existing list: {settings.Name} · revision {watch.Definition.Revision} · {settings.Mode}/{settings.Backend} · target {settings.ControllerTargetId ?? "none"} · interval {settings.IntervalMs} ms · history {settings.HistoryLimit}", AutoSize = true, Padding = new(0, 6, 0, 0) };
        top.Controls.Add(context); top.SetFlowBreak(context, true);
        top.Controls.Add(new Label { Text = "Destination snapshot", AutoSize = true, Padding = new(0, 7, 0, 0) }); top.Controls.Add(projects); Button(top, "Refresh snapshots", InitializeAsync);
        top.SetFlowBreak(top.Controls[top.Controls.Count - 1], true);
        top.Controls.Add(new Label { Text = "Program mappings below: leave a destination blank for controller scope.", AutoSize = true });
        projects.Format += (_, e) => { if (e.ListItem is ProjectInfo p) e.Value = $"{p.Name} · r{p.Revision} · {p.Id[..Math.Min(8, p.Id.Length)]}"; };
        projects.SelectedIndexChanged += (_, _) => InvalidatePreview();
        mappings.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Original scope", ReadOnly = true }); mappings.Columns.Add("Destination", "Destination program");
        changes.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        foreach (var scope in settings.Tags.Select(t => t.ProgramName).Distinct(StringComparer.Ordinal))
        { var row = mappings.Rows[mappings.Rows.Add(scope ?? "(Controller scope)", scope ?? "")]; row.Tag = scope; }
        mappings.CellValueChanged += (_, _) => InvalidatePreview();
        var upper = new SplitContainer { Dock = DockStyle.Fill }; upper.Panel1.Controls.Add(mappings); upper.Panel2.Controls.Add(changes);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal }; split.Panel1.Controls.Add(upper); split.Panel2.Controls.Add(detail);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(8) };
        Button(actions, "Preview rebind", PreviewAsync); Button(actions, "Preflight details…", () =>
        {
            if (preview is null) throw new ArgumentException("Preview the destination first.");
            using var form = new WatchPreflightForm(preview.Preflight, settings.Name); form.ShowDialog(this); return Task.CompletedTask;
        });
        actions.Controls.Add(apply); apply.Click += async (_, _) => await RunAsync(ApplyAsync);
        Controls.Add(split); Controls.Add(actions); Controls.Add(status); Controls.Add(top);
        Shown += async (_, _) => { upper.SplitterDistance = Math.Max(350, upper.Width / 3); split.SplitterDistance = Math.Max(180, split.Height / 2); if (!fixture) await RunAsync(InitializeAsync); };
    }
    private void Button(Control parent, string text, Func<Task> action)
    { var button = new Button { Text = text, AutoSize = true }; button.Click += async (_, _) => await RunAsync(action); parent.Controls.Add(button); }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return; busy = true; Enabled = false; UseWaitCursor = true;
        try { await action(); } catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; Enabled = true; UseWaitCursor = false; }
    }
    private void InvalidatePreview()
    {
        if (suppress) return; reviewed = null; preview = null; apply.Enabled = false; changes.DataSource = null;
        detail.Text = "Destination/mapping changed. Preview again before applying.";
    }
    private async Task InitializeAsync()
    {
        var id = (projects.SelectedItem as ProjectInfo)?.Id ?? initial?.Id;
        var items = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").ToArray();
        suppress = true; projects.DataSource = items; projects.SelectedItem = items.FirstOrDefault(p => p.Id == id) ?? items.FirstOrDefault(); suppress = false; InvalidatePreview();
    }
    private async Task PreviewAsync()
    {
        if (projects.SelectedItem is not ProjectInfo selected) throw new ArgumentException("Choose a destination Logix snapshot.");
        mappings.EndEdit(); var project = await client.GetAsync<ProjectInfo>("/api/projects/" + selected.Id);
        var maps = mappings.Rows.Cast<DataGridViewRow>().Select(row => new WatchTemplateProgramMap((string?)row.Tag,
            string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["Destination"].Value)) ? null : Convert.ToString(row.Cells["Destination"].Value)!.Trim()))
            .Where(m => m.SourceProgram != m.DestinationProgram).ToArray();
        var request = new WatchRebindPreviewRequest(watch.Definition.Revision, project.Id, project.CurrentSha256, maps);
        var report = await client.PostAsync<WatchRebindPreviewReport>($"/api/watches/{watch.Definition.Id}/rebind-preview", request);
        reviewed = request; ShowPreview(report);
    }
    private void ShowPreview(WatchRebindPreviewReport report)
    {
        preview = report; apply.Enabled = report.CanApply;
        string Shape(WatchDeclarationShape? shape) => shape is null ? "unavailable" : shape.DataType + (shape.Dimensions.Length == 0 ? "" : "[" + string.Join(',', shape.Dimensions) + "]");
        changes.DataSource = report.Changes.Select(c => new { c.Before.Key, BeforeType = Shape(c.BeforeDeclaration), AfterType = Shape(c.AfterDeclaration),
            Program = c.After.ProgramName ?? "(Controller scope)", Changes = string.Join(", ", c.Changes) }).ToArray();
        detail.Text = $"{(report.CanApply ? "READY TO REBIND THE STOPPED LIST" : "DESTINATION NEEDS CORRECTION")}\r\n" +
            $"Watch: {report.WatchId} · revision {report.FromRevision} -> {report.FromRevision + 1}\r\nBaseline: {report.BaselineStatus}\r\n" +
            $"Old approved SHA-256: {report.FromExpectedSha256}\r\nOld observed SHA-256: {report.FromObservedSha256 ?? "unavailable"}\r\n" +
            $"New source SHA-256: {report.Proposed.ExpectedSha256}\r\nReview SHA-256: {report.ReviewSha256}\r\n\r\n" +
            string.Join("\r\n", report.Blockers.Select(i => i.Code + ": " + i.Message)) + "\r\n" +
            string.Join("\r\n", report.Changes.Select(c => c.Before.Key + ": " + string.Join(", ", c.Changes))) + "\r\n" +
            string.Join("\r\n", report.Changes.SelectMany(c => c.Notes.Select(n => c.Before.Key + ": " + n))) + "\r\n\r\n" + report.Coverage;
        status.Text = report.CanApply ? "Review declaration/scope changes. Applying advances the watch revision, keeps retained recordings and clears cached values. Start polling separately." : "Resolve destination diagnostics, then preview again.";
    }
    private async Task ApplyAsync()
    {
        if (preview is not { CanApply: true } || reviewed is null) throw new ArgumentException("Preview the current destination first.");
        ReboundWatch = await client.PostAsync<WatchStatus>($"/api/watches/{watch.Definition.Id}/rebind", new WatchRebindApplyRequest(reviewed, preview.ReviewSha256));
        DialogResult = DialogResult.OK; Close();
    }
    public void LoadForTest(WatchRebindPreviewReport report)
    {
        fixture = true; suppress = true;
        projects.DataSource = new[] { new ProjectInfo(report.Proposed.ProjectId, "Generated destination", "Fixture.L5X", report.Proposed.ExpectedSha256, report.Proposed.ExpectedSha256, 1, DateTimeOffset.UtcNow) };
        suppress = false; ShowPreview(report); apply.Enabled = false;
    }
}
