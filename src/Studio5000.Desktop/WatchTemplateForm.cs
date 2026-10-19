using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class WatchTemplateForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly ComboBox projects = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly ComboBox mode = new() { Width = 95, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox backend = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox target = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox name = new() { Width = 220 };
    private readonly Label file = new() { Width = 640, AutoEllipsis = true, Padding = new(0, 7, 0, 0), Text = "Choose a portable watch template JSON file." };
    private readonly DataGridView mappings = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly DataGridView selections = new() { Dock = DockStyle.Fill, ReadOnly = true, RowHeadersVisible = false,
        AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox result = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 58, Padding = new(8) };
    private readonly Button create = new() { Text = "Create stopped watch list", AutoSize = true, Enabled = false };
    private WatchTemplate? template;
    private JsonElement document;
    private WatchTemplatePreviewRequest? reviewedRequest;
    private WatchTemplatePreviewReport? preview;
    private bool busy, suppress, fixture;
    public WatchStatus? ImportedWatch { get; private set; }

    public WatchTemplateForm(ServiceClient client, ProjectInfo? initial)
    {
        this.client = client; this.initial = initial;
        Text = "Import portable watch template"; Size = new(1320, 880); MinimumSize = new(1080, 740); Font = new("Segoe UI", 10);
        projects.FormattingEnabled = true;
        projects.Format += (_, e) => { if (e.ListItem is ProjectInfo p) e.Value = $"{p.Name} · r{p.Revision} · {p.Id[..Math.Min(8, p.Id.Length)]}"; };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 152, Padding = new(8) };
        Button(top, "Choose template…", OpenAsync); top.Controls.Add(file); top.SetFlowBreak(file, true);
        top.Controls.Add(new Label { Text = "Destination snapshot", AutoSize = true, Padding = new(0, 7, 0, 0) }); top.Controls.Add(projects);
        Button(top, "Refresh projects", InitializeAsync); top.SetFlowBreak(top.Controls[^1], true);
        top.Controls.Add(new Label { Text = "New list name", AutoSize = true, Padding = new(0, 7, 0, 0) }); top.Controls.Add(name);
        backend.Items.AddRange(["Sdk", "LibPlcTag"]); backend.SelectedIndex = 0; top.Controls.Add(backend);
        mode.Items.AddRange(["Offline", "Online"]); mode.SelectedIndex = 0; top.Controls.Add(mode); top.Controls.Add(target);
        var upper = new SplitContainer { Dock = DockStyle.Fill };
        var mapPanel = new Panel { Dock = DockStyle.Fill }; mapPanel.Controls.Add(mappings);
        mapPanel.Controls.Add(new Label { Text = "PROGRAM MAPPING • blank destination = controller scope", Dock = DockStyle.Top, Height = 32, Padding = new(5) }); upper.Panel1.Controls.Add(mapPanel);
        var selectionPanel = new Panel { Dock = DockStyle.Fill }; selectionPanel.Controls.Add(selections);
        selectionPanel.Controls.Add(new Label { Text = "REVIEWED DESTINATION SELECTIONS", Dock = DockStyle.Top, Height = 32, Padding = new(5) }); upper.Panel2.Controls.Add(selectionPanel);
        mappings.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Source scope", ReadOnly = true });
        mappings.Columns.Add("Destination", "Destination program (blank = controller)");
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(upper); split.Panel2.Controls.Add(result);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(8) };
        Button(actions, "Preview binding", PreviewAsync); Button(actions, "Preflight details…", () =>
        {
            if (preview is null) throw new ArgumentException("Preview the current binding first.");
            using var form = new WatchPreflightForm(preview.Preflight, name.Text); form.ShowDialog(this); return Task.CompletedTask;
        });
        actions.Controls.Add(create); create.Click += async (_, _) => await RunAsync(ImportAsync);
        Controls.Add(split); Controls.Add(actions); Controls.Add(status); Controls.Add(top);
        name.TextChanged += (_, _) => InvalidatePreview(); projects.SelectedIndexChanged += (_, _) => InvalidatePreview();
        mode.SelectedIndexChanged += (_, _) => InvalidatePreview(); backend.SelectedIndexChanged += (_, _) => InvalidatePreview(); target.SelectedIndexChanged += (_, _) => InvalidatePreview();
        mappings.CellValueChanged += (_, _) => InvalidatePreview();
        Shown += async (_, _) =>
        {
            split.SplitterDistance = Math.Max(180, split.Height / 2); upper.SplitterDistance = upper.Width / 2;
            if (!fixture) await RunAsync(InitializeAsync);
        };
        status.Text = "Bind selections to a saved Logix snapshot. Preview before creating a new stopped list. CIP requires Online and a configured target.";
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
        if (suppress) return; preview = null; reviewedRequest = null; create.Enabled = false; selections.DataSource = null;
        result.Text = "Binding changed. Preview again before creating a list.";
    }
    private async Task InitializeAsync()
    {
        var selectedId = (projects.SelectedItem as ProjectInfo)?.Id ?? initial?.Id;
        var selectedTarget = target.Text;
        var items = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").ToArray();
        var caps = await client.GetAsync<JsonElement>("/api/sdk/capabilities");
        suppress = true; projects.DataSource = items; projects.SelectedItem = items.FirstOrDefault(p => p.Id == selectedId) ?? items.FirstOrDefault();
        target.Items.Clear(); target.Items.Add(""); foreach (var item in caps.GetProperty("controllerTargets").EnumerateArray()) target.Items.Add(item.GetProperty("id").GetString()!);
        target.SelectedItem = selectedTarget; if (target.SelectedIndex < 0) target.SelectedIndex = 0;
        suppress = false; InvalidatePreview();
    }
    private async Task OpenAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Watch template JSON|*.json" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(dialog.FileName).Length > WatchTemplates.MaximumBytes) throw new ArgumentException("Template exceeds 256 KiB.");
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(dialog.FileName));
        LoadTemplate(json.RootElement.Clone(), dialog.FileName);
    }
    private void LoadTemplate(JsonElement value, string label)
    {
        var parsed = WatchTemplates.Parse(value); template = parsed; document = value;
        suppress = true; file.Text = label; name.Text = parsed.Name; backend.SelectedItem = parsed.Backend; mode.SelectedItem = "Offline";
        mappings.Rows.Clear();
        foreach (var scope in parsed.Tags.Select(t => t.ProgramName).Distinct(StringComparer.Ordinal))
        {
            var row = mappings.Rows[mappings.Rows.Add(scope ?? "(Controller scope)", scope ?? "")]; row.Tag = scope;
        }
        suppress = false; InvalidatePreview(); status.Text = $"{parsed.Tags.Length} selections • interval {parsed.IntervalMs} ms • history {parsed.HistoryLimit}. Review program mappings and destination before import.";
    }
    private async Task PreviewAsync()
    {
        if (template is null || projects.SelectedItem is not ProjectInfo selected) throw new ArgumentException("Choose a template and destination snapshot first.");
        mappings.EndEdit();
        var project = await client.GetAsync<ProjectInfo>("/api/projects/" + selected.Id);
        var maps = mappings.Rows.Cast<DataGridViewRow>().Select(row => new WatchTemplateProgramMap((string?)row.Tag,
            string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["Destination"].Value)) ? null : Convert.ToString(row.Cells["Destination"].Value)!.Trim()))
            .Where(m => m.SourceProgram != m.DestinationProgram).ToArray();
        var request = new WatchTemplatePreviewRequest(document, project.Id, project.CurrentSha256, name.Text.Trim(), maps, mode.Text,
            string.IsNullOrWhiteSpace(target.Text) ? null : target.Text, backend.Text);
        var report = await client.PostAsync<WatchTemplatePreviewReport>("/api/watch-templates/preview", request);
        reviewedRequest = request; ShowPreview(report);
    }
    private void ShowPreview(WatchTemplatePreviewReport report)
    {
        preview = report; create.Enabled = report.CanImport;
        selections.DataSource = report.Preflight.Selections.Select(s => new { s.Tag.Key, Program = s.Tag.ProgramName ?? "(Controller scope)", s.Tag.TagName, s.Tag.MemberPath, s.Status }).ToArray();
        result.Text = $"{(report.CanImport ? "READY TO CREATE A STOPPED LIST" : "BINDING NEEDS CORRECTION")}\r\n" +
            $"Destination source SHA-256: {report.Proposed.ExpectedSha256}\r\nTemplate SHA-256: {report.TemplateSha256}\r\nReview SHA-256: {report.ReviewSha256}\r\n" +
            $"New list: {report.Proposed.Name} • {report.Proposed.Mode}/{report.Proposed.Backend} • {report.Proposed.IntervalMs} ms • history {report.Proposed.HistoryLimit}\r\n\r\n" +
            string.Join("\r\n", report.ImportBlockers.Select(i => i.Code + ": " + i.Message)) + "\r\n\r\n" +
            string.Join("\r\n", report.Preflight.Checks.Where(c => c.Status != "passed").Select(c => c.Name + ": " + c.Issue?.Message)) + "\r\n\r\n" + report.Coverage;
        status.Text = report.CanImport ? "Binding checked. Creating the list leaves polling stopped; use Preflight details for polling availability." : "Correct the binding diagnostics, then preview again.";
    }
    private async Task ImportAsync()
    {
        if (preview is not { CanImport: true } || reviewedRequest is null) throw new ArgumentException("Preview the current binding first.");
        ImportedWatch = await client.PostAsync<WatchStatus>("/api/watch-templates/import", new WatchTemplateImportRequest(reviewedRequest, preview.ReviewSha256));
        DialogResult = DialogResult.OK; Close();
    }
    public void LoadForTest(JsonElement value, WatchTemplatePreviewReport report)
    {
        fixture = true; LoadTemplate(value, "Generated WatchTemplateDemo.json");
        suppress = true; projects.DataSource = new[] { new ProjectInfo(report.Proposed.ProjectId, "Generated Logix snapshot", "Fixture.L5X", report.Proposed.ExpectedSha256, report.Proposed.ExpectedSha256, 0, DateTimeOffset.UtcNow) };
        suppress = false; ShowPreview(report); create.Enabled = false;
    }
}
