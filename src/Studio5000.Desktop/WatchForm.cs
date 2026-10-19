using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class WatchForm : Form
{
    private readonly ServiceClient client;
    private readonly ProjectInfo? project;
    private readonly ListBox lists = new() { Dock = DockStyle.Fill, DisplayMember = "Label" };
    private readonly TextBox name = new() { Width = 200, Text = "New watch list" };
    private readonly NumericUpDown interval = new() { Minimum = 100, Maximum = 86400000, Value = 1000, Increment = 100, Width = 100 };
    private readonly ComboBox mode = new() { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox backend = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox target = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox source = new() { Width = 620, ReadOnly = true, PlaceholderText = "Choose a matching saved ACD or L5X" };
    private readonly DataGridView tags = new() { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly DataGridView values = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AllowUserToAddRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false };
    private readonly TreeView tree = new() { Dock = DockStyle.Fill };
    private readonly TextBox structures = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly TextBox history = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly TextBox uploadJob = new() { Width = 260, PlaceholderText = "Upload SDK job ID" };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 52, Padding = new(8) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private WatchStatus? selected;
    private string? sourcePath, artifactId, sourceHash;
    private WatchValue[] displayed = [];
    private bool busy, suppress;
    private sealed record ListRow(string Id, string Label);

    public WatchForm(ServiceClient client, ProjectInfo? project)
    {
        this.client = client; this.project = project;
        Text = "Watch lists and UDT structures"; Size = new(1280, 850); MinimumSize = new(1050, 650); Font = new("Segoe UI", 10);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new(6) };
        Button(toolbar, "Refresh lists", RefreshAsync); Button(toolbar, "New", () => { New(); return Task.CompletedTask; });
        Button(toolbar, "Save list", SaveAsync); Button(toolbar, "Preflight draft…", PreflightAsync); Button(toolbar, "Start polling", () => ActionAsync("start")); Button(toolbar, "Stop polling", () => ActionAsync("stop"));
        Button(toolbar, "Delete stopped list", DeleteAsync); Button(toolbar, "History", HistoryAsync);
        Button(toolbar, "Tag / UDT browser…", BrowserAsync); Button(toolbar, "Trends & recording…", () =>
        {
            if (selected is null) throw new ArgumentException("Save or select a watch list first.");
            using var form = new TrendForm(client, selected); form.ShowDialog(this); return Task.CompletedTask;
        });
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(lists);
        var settings = new TabPage("List settings");
        var fields = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 140, Padding = new(6), WrapContents = true };
        fields.Controls.Add(new Label { Text = "Name", AutoSize = true, Padding = new(0, 7, 0, 0) }); fields.Controls.Add(name);
        fields.Controls.Add(new Label { Text = "Interval ms", AutoSize = true, Padding = new(0, 7, 0, 0) }); fields.Controls.Add(interval);
        mode.Items.AddRange(["Online", "Offline"]); mode.SelectedIndex = 0; fields.Controls.Add(mode); fields.SetFlowBreak(mode, true);
        fields.Controls.Add(new Label { Text = "Reader", AutoSize = true, Padding = new(0, 7, 0, 0) });
        backend.Items.AddRange(["Sdk", "LibPlcTag"]); backend.SelectedIndex = 0; fields.Controls.Add(backend); fields.Controls.Add(target);
        fields.SetFlowBreak(target, true); fields.Controls.Add(source); Button(fields, "Choose source…", ChooseSourceAsync);
        foreach (var field in new[] { "Key", "Tag", "Program", "Member" }) tags.Columns.Add(field, field == "Member" ? "Member (optional)" : field);
        settings.Controls.Add(tags); settings.Controls.Add(fields);
        settings.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 68, Padding = new(6), Text = "Use exact base tags and optional members (Inner.Count or Samples[1]). LibPlcTag requires Online + L5X + configured CIP target. Use the browser to plan reads. Saving does not connect." });
        var live = new TabPage("Latest values"); var liveSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        liveSplit.Panel1.Controls.Add(values); liveSplit.Panel2.Controls.Add(tree); live.Controls.Add(liveSplit);
        var schema = new TabPage("Extracted structures"); schema.Controls.Add(structures);
        var schemaBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 80, Padding = new(6) };
        Button(schemaBar, "Extract selected project", ExtractAsync); Button(schemaBar, "Pull controller UDTs", PullAsync);
        schemaBar.Controls.Add(uploadJob); Button(schemaBar, "Extract upload result", ExtractUploadAsync); schema.Controls.Add(schemaBar);
        var historyTab = new TabPage("Polling history"); historyTab.Controls.Add(history);
        tabs.TabPages.AddRange([settings, live, schema, historyTab]); split.Panel2.Controls.Add(tabs);
        Controls.Add(split); Controls.Add(status); Controls.Add(toolbar);
        lists.SelectedIndexChanged += async (_, _) => { if (!suppress && lists.SelectedItem is ListRow row) await RunAsync(async () => LoadWatch(await client.GetAsync<WatchStatus>("/api/watches/" + row.Id))); };
        values.SelectionChanged += (_, _) => RenderTree();
        timer.Tick += async (_, _) => { if (!busy && selected is not null) await RunAsync(async () => { selected = await client.GetAsync<WatchStatus>("/api/watches/" + selected.Definition.Id); Render(selected); }); };
        Shown += async (_, _) => { split.SplitterDistance = 230; liveSplit.SplitterDistance = Math.Max(180, liveSplit.Height / 2); await RunAsync(InitializeAsync); timer.Start(); };
        FormClosed += (_, _) => timer.Dispose(); // Service owns polling; closing this viewer does not stop shared lists.
        New();
    }
    private void Button(Control parent, string text, Func<Task> action)
    { var button = new Button { Text = text, AutoSize = true }; button.Click += async (_, _) => await RunAsync(action); parent.Controls.Add(button); }
    private async Task RunAsync(Func<Task> action)
    { if (busy) return; busy = true; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; } }
    private async Task InitializeAsync()
    {
        var caps = await client.GetAsync<JsonElement>("/api/sdk/capabilities");
        target.Items.Clear(); target.Items.Add(""); foreach (var item in caps.GetProperty("controllerTargets").EnumerateArray()) target.Items.Add(item.GetProperty("id").GetString()!);
        target.SelectedIndex = target.Items.Count > 1 ? 1 : 0;
        status.Text = "Controller access enabled: " + caps.GetProperty("controllerOperationsEnabled") + ". Lists remain stopped until Start. Closing this window leaves shared polling running.";
        await RefreshAsync();
    }
    private void New()
    {
        tabs.SelectedIndex = 0;
        selected = null; sourcePath = artifactId = sourceHash = null; source.Text = ""; name.Text = "New watch list";
        backend.SelectedItem = "Sdk";
        tags.Rows.Clear(); tags.Rows.Add("value", "", "", ""); values.DataSource = null; tree.Nodes.Clear();
    }
    private async Task RefreshAsync()
    {
        var items = await client.GetAsync<WatchStatus[]>("/api/watches"); var id = selected?.Definition.Id;
        suppress = true; lists.DataSource = items.Select(w => new ListRow(w.Definition.Id, w.Definition.Settings.Name + " · " + w.State)).ToArray();
        lists.SelectedItem = ((ListRow[])lists.DataSource).FirstOrDefault(r => r.Id == id); suppress = false;
    }
    private void LoadWatch(WatchStatus value)
    {
        selected = value; var r = value.Definition.Settings; name.Text = r.Name; interval.Value = r.IntervalMs; mode.SelectedItem = r.Mode; backend.SelectedItem = r.Backend;
        target.SelectedItem = r.ControllerTargetId ?? ""; sourcePath = r.SourcePath; artifactId = r.SourceArtifactId; sourceHash = r.ExpectedSha256;
        source.Text = sourcePath ?? "artifact:" + artifactId; tags.Rows.Clear();
        foreach (var t in r.Tags) tags.Rows.Add(t.Key, t.TagName, t.ProgramName ?? "", t.MemberPath ?? ""); Render(value);
    }
    private async Task ChooseSourceAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Logix watch source|*.acd;*.l5x" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var info = await client.PostAsync<JsonElement>("/api/sdk/source", new RegisterRequest(dialog.FileName));
        sourcePath = dialog.FileName; artifactId = null; sourceHash = info.GetProperty("sha256").GetString(); source.Text = sourcePath;
    }
    private WatchListRequest ReadDraft()
    {
        tags.EndEdit();
        string Cell(DataGridViewRow row, string column) => Convert.ToString(row.Cells[column].Value)?.Trim() ?? "";
        string? Optional(string value) => value.Length == 0 ? null : value;
        var items = tags.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new WatchTag(Cell(r, "Key"), Cell(r, "Tag"), Optional(Cell(r, "Program")), Optional(Cell(r, "Member")))).ToArray();
        return new(name.Text.Trim(), items, sourceHash ?? "", sourcePath, artifactId, Optional(target.Text), mode.Text, (int)interval.Value,
            HistoryLimit: selected?.Definition.Settings.HistoryLimit ?? 100, Backend: backend.Text);
    }
    private async Task PreflightAsync()
    {
        var report = await client.PostAsync<WatchPreflightReport>("/api/watches/preflight", new WatchPreflightRequest(Settings: ReadDraft()));
        using var form = new WatchPreflightForm(report, name.Text); form.ShowDialog(this);
    }
    private async Task SaveAsync()
    {
        var request = ReadDraft();
        var result = selected is null ? await client.PostAsync<WatchStatus>("/api/watches", request) :
            await client.PostAsync<WatchStatus>("/api/watches/" + selected.Definition.Id, new WatchUpdateRequest(request, selected.Definition.Revision));
        LoadWatch(result); await RefreshAsync();
    }
    private async Task ActionAsync(string action)
    { if (selected is null) throw new ArgumentException("Save or select a list first."); selected = await client.PostAsync<WatchStatus>("/api/watches/" + selected.Definition.Id + "/" + action, new { }); Render(selected); await RefreshAsync(); }
    private async Task DeleteAsync()
    { if (selected is null) return; await client.PostAsync<JsonElement>("/api/watches/" + selected.Definition.Id + "/delete", new { }); New(); await RefreshAsync(); }
    private async Task HistoryAsync()
    { if (selected is not null) { history.Text = JsonSerializer.Serialize(await client.GetAsync<WatchSnapshot[]>("/api/watches/" + selected.Definition.Id + "/history"), ProjectService.Json); tabs.SelectedIndex = 3; } }
    private async Task BrowserAsync()
    {
        using var browser = new TagBrowserForm(client, project); browser.ShowDialog(this);
        if (browser.CreatedWatch is { } created) { LoadWatch(created); await RefreshAsync(); tabs.SelectedIndex = 0; }
    }
    private async Task ExtractAsync()
    {
        if (project is null) throw new ArgumentException("Select a Logix project in the main window first.");
        structures.Text = JsonSerializer.Serialize(await client.PostAsync<StructureExtraction>("/api/structures/extract", new StructureExtractRequest(ProjectId: project.Id)), ProjectService.Json);
    }
    private async Task PullAsync()
    { var job = await client.PostAsync<SdkJob>("/api/structures/pull", new StructurePullRequest(target.Text)); uploadJob.Text = job.Id; status.Text = "Upload queued. Extract upload result after its SDK job completes."; }
    private async Task ExtractUploadAsync() => structures.Text = JsonSerializer.Serialize(await client.PostAsync<StructureExtraction>("/api/structures/extract", new StructureExtractRequest(JobId: uploadJob.Text.Trim())), ProjectService.Json);
    private void Render(WatchStatus watch)
    {
        displayed = watch.Latest?.Values ?? [];
        values.DataSource = displayed.Select(v => new { v.Key, v.TagName, v.ProgramName, v.DataType, Value = v.Value?.ToJsonString(), v.Quality, v.Error }).ToArray();
        structures.Text = JsonSerializer.Serialize(watch.Types, ProjectService.Json);
        status.Text = $"{watch.State} · {(watch.Stale ? "STALE" : "current")} · {watch.Definition.Settings.Mode}/{watch.Definition.Settings.Backend} · sample {watch.Latest?.Sequence} · {watch.Latest?.CompletedAt.ToLocalTime():T} · {watch.Error}";
        RenderTree();
    }
    private void RenderTree()
    {
        tree.Nodes.Clear(); var i = values.CurrentRow?.Index ?? -1; if (i < 0 || i >= displayed.Length) return;
        TreeNode Node(string label, JsonNode? value)
        {
            var node = new TreeNode(label);
            if (value is JsonObject obj) foreach (var item in obj) node.Nodes.Add(Node(item.Key, item.Value));
            else if (value is JsonArray array) for (var index = 0; index < array.Count; index++) node.Nodes.Add(Node("[" + index + "]", array[index]));
            else node.Text += " = " + (value?.ToJsonString() ?? "null");
            return node;
        }
        tree.Nodes.Add(Node(displayed[i].Key, displayed[i].Value)); tree.ExpandAll();
    }
    public void LoadForTest(WatchStatus fixture, bool showValues = false) { LoadWatch(fixture); tabs.SelectedIndex = showValues ? 1 : 0; }
}
