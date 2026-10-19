using System.Xml.Linq;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class GraphicsReferencesForm : WorkspaceForm
{
    private sealed record ShortcutRow(GraphicsShortcutBinding Binding, string Snapshot)
    {
        public string Shortcut => Binding.Shortcut;
    }
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly ComboBox display = Choice(400), plc = Choice(360);
    private readonly TextBox shortcut = new() { Width = 100, PlaceholderText = "Shortcut" };
    private readonly ComboBox scope = new() { Width = 650, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox parameter = new() { Width = 70, PlaceholderText = "#1" };
    private readonly TextBox value = new() { Width = 440, PlaceholderText = "Literal value, e.g. {[PLC]Program:Example.Record}" };
    private readonly TextBox query = new() { Width = 420, PlaceholderText = "Filter original/expanded reference, object or status" };
    private readonly DataGridView shortcuts = Grid(), parameters = Grid(), references = Grid();
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Font = new("Consolas", 10) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 50, Padding = new(8),
        Text = "Choose a display and select Resolve to inspect its references and available parameter scopes." };
    private readonly List<ShortcutRow> shortcutRows = [];
    private readonly List<GraphicsParameterBinding> parameterRows = [];
    private readonly FlowLayoutPanel top = new() { Dock = DockStyle.Top, Height = 90, Padding = new(8) };
    private readonly TabControl context = new() { Dock = DockStyle.Fill };
    private GraphicsResolveRequest? request;
    private GraphicsResolutionReport? report;
    private bool busy, fixture;

    public GraphicsReferencesForm(ServiceClient client, ProjectInfo? initial)
    {
        this.client = client; this.initial = initial;
        Text = "FactoryTalk PLC/HMI reference resolution"; Size = new(1500, 940); MinimumSize = new(1200, 780); Font = new("Segoe UI", 10);
        top.Controls.Add(new Label { Text = "ME / SE display", AutoSize = true, Padding = new(0, 7, 0, 0) }); top.Controls.Add(display);
        Button(top, "Refresh snapshots", RefreshAsync); Button(top, "Resolve", ResolveAsync);
        Button(top, "Previous", () => PageAsync(-1)); Button(top, "Next", () => PageAsync(1));
        Button(top, "Where used in PLC…", async () =>
        {
            var index = references.CurrentRow?.Index ?? -1;
            if (report is null || index < 0 || index >= report.References.Length || report.References[index] is not
                { Status: "matched_in_snapshot", ProjectId: not null, ProjectSha256: not null, TagName: not null } row)
                throw new InvalidOperationException("Resolve and select a matched PLC reference first.");
            var current = await client.GetAsync<ProjectInfo>($"/api/projects/{row.ProjectId}");
            if (current.CurrentSha256 != row.ProjectSha256) throw new InvalidOperationException("Mapped PLC snapshot changed. Refresh and resolve the display again.");
            using var whereUsed = new TagReferencesForm(client, current, row.TagName, row.ProgramName); whereUsed.ShowDialog(this);
        });
        top.SetFlowBreak(top.Controls[^1], true); top.Controls.Add(Caption("Filter")); top.Controls.Add(query);
        top.Controls.Add(new Label { Text = "Saved PLC metadata • explicit context • sources preserved", AutoSize = true, Padding = new(8, 7, 0, 0) });

        var shortcutPage = new TabPage("PLC shortcuts"); var shortcutBar = Bar(86);
        shortcutBar.Controls.Add(Caption("Shortcut")); shortcutBar.Controls.Add(shortcut); shortcutBar.Controls.Add(Caption("Snapshot")); shortcutBar.Controls.Add(plc);
        Button(shortcutBar, "Set mapping", SetShortcutAsync);
        Button(shortcutBar, "Remove selected", () => { if (shortcuts.CurrentRow?.DataBoundItem is ShortcutRow row) shortcutRows.Remove(row); ContextChanged(); return Task.CompletedTask; });
        shortcutBar.SetFlowBreak(shortcutBar.Controls[^1], true);
        shortcutBar.Controls.Add(new Label { AutoSize = true, Text = "Mappings pin the selected L5X revision/hash. Refresh clears context; reselect after a stale hash.", Padding = new(0, 5, 0, 0) });
        Column(shortcuts, "Shortcut", "Shortcut", 150); Column(shortcuts, "Snapshot", "Saved Logix snapshot", fill: true);
        shortcutPage.Controls.Add(shortcuts); shortcutPage.Controls.Add(shortcutBar);

        var parameterPage = new TabPage("Scoped parameters"); var parameterBar = Bar(120);
        parameterBar.Controls.Add(new Label { Text = "Exact scope", AutoSize = true, Padding = new(0, 7, 0, 0) }); parameterBar.Controls.Add(scope);
        parameterBar.SetFlowBreak(scope, true); parameterBar.Controls.Add(Caption("Parameter")); parameterBar.Controls.Add(parameter);
        parameterBar.Controls.Add(Caption("Value")); parameterBar.Controls.Add(value);
        Button(parameterBar, "Set binding", SetParameterAsync);
        Button(parameterBar, "Remove selected", () => { if (parameters.CurrentRow?.DataBoundItem is GraphicsParameterBinding row) parameterRows.Remove(row); ContextChanged(); return Task.CompletedTask; });
        parameterBar.SetFlowBreak(parameterBar.Controls[^1], true);
        parameterBar.Controls.Add(new Label { Text = "Resolve first for scope choices. Nearest supplied ancestor wins; exported values are never assumed.", AutoSize = true, Padding = new(0, 4, 0, 0) });
        Column(parameters, "ScopeXPath", "Exact object / group scope", fill: true); Column(parameters, "Parameter", "Parameter", 100); Column(parameters, "Value", "Literal value", fill: true);
        parameterPage.Controls.Add(parameters); parameterPage.Controls.Add(parameterBar);
        parameters.SelectionChanged += (_, _) =>
        {
            if (parameters.CurrentRow?.DataBoundItem is not GraphicsParameterBinding binding) return;
            scope.Text = binding.ScopeXPath; parameter.Text = binding.Parameter; value.Text = binding.Value;
        };
        context.TabPages.Add(shortcutPage); context.TabPages.Add(parameterPage);

        Column(references, "Status", "Status", 270); Column(references, "Object", "Object", 150);
        Column(references, "Original", "Original reference", fill: true); Column(references, "Expanded", "Expanded reference", fill: true); Column(references, "Type", "Type / array", 130);
        references.SelectionChanged += (_, _) => SelectReference();
        var body = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        body.Panel1.Controls.Add(context);
        var result = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        result.Panel1.Controls.Add(references); result.Panel2.Controls.Add(details); body.Panel2.Controls.Add(result);
        Controls.Add(body); Controls.Add(top); Controls.Add(status);
        display.SelectionChangeCommitted += (_, _) => ClearContext();
        Shown += async (_, _) =>
        {
            body.SplitterDistance = Math.Max(240, body.Height * 2 / 5);
            result.SplitterDistance = Math.Max(120, result.Height / 2);
            if (!fixture) await Run(RefreshAsync);
        };
    }
    private static ComboBox Choice(int width) => new() { Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new(0, 6, 0, 0) };
    private static FlowLayoutPanel Bar(int height) => new() { Dock = DockStyle.Top, Height = height, Padding = new(6) };
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true,
        AllowUserToAddRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private static void Column(DataGridView grid, string property, string title, int width = 100, bool fill = false) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None });
    private void Button(FlowLayoutPanel parent, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true }; parent.Controls.Add(button); button.Click += async (_, _) => await Run(action);
    }
    private async Task Run(Func<Task> action)
    {
        if (busy || fixture) return; busy = true; top.Enabled = context.Enabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            report = null; request = null; references.DataSource = null; details.Clear();
            status.Text = "Request failed. Refresh and rebuild context after a stale snapshot."; MessageBox.Show(this, ex.Message, Text);
        }
        finally { busy = false; top.Enabled = context.Enabled = true; }
    }
    private async Task RefreshAsync()
    {
        var projects = await client.GetAsync<ProjectInfo[]>("/api/projects");
        var selected = (display.SelectedItem as ReviewProjectChoice)?.Project.Id ?? initial?.Id;
        var displays = projects.Where(p => FactoryTalkGraphics.IsFactoryTalk(p.Kind)).Select(p => new ReviewProjectChoice(p)).ToArray();
        display.DataSource = displays; plc.DataSource = projects.Where(p => p.Kind == "logix").Select(p => new ReviewProjectChoice(p)).ToArray();
        if (displays.FirstOrDefault(d => d.Project.Id == selected) is { } choice) display.SelectedItem = choice;
        ClearContext(); status.Text = $"{displays.Length} ME/SE displays. Resolve without context to inspect references; add explicit mappings and bindings as needed.";
    }
    private void ClearContext()
    {
        shortcutRows.Clear(); parameterRows.Clear(); scope.Items.Clear(); scope.Text = ""; ContextChanged();
    }
    private void ContextChanged()
    {
        shortcuts.DataSource = shortcutRows.ToArray(); parameters.DataSource = parameterRows.ToArray();
        request = null; report = null; references.DataSource = null; details.Clear(); status.Text = "Context changed. Select Resolve to check the selected snapshots.";
    }
    private Task SetShortcutAsync()
    {
        if (plc.SelectedItem is not ReviewProjectChoice choice || string.IsNullOrWhiteSpace(shortcut.Text))
            throw new InvalidOperationException("Enter the shortcut name and select a saved Logix snapshot.");
        var name = shortcut.Text.Trim(); shortcutRows.RemoveAll(r => r.Shortcut.Equals(name, StringComparison.OrdinalIgnoreCase));
        shortcutRows.Add(new(new(name, choice.Project.Id, choice.Project.CurrentSha256), choice.ToString())); ContextChanged(); return Task.CompletedTask;
    }
    private Task SetParameterAsync()
    {
        if (string.IsNullOrWhiteSpace(scope.Text) || string.IsNullOrWhiteSpace(parameter.Text))
            throw new InvalidOperationException("Select/paste an exact scope and enter a #number parameter.");
        var path = scope.Text.Trim(); var name = parameter.Text.Trim();
        parameterRows.RemoveAll(r => r.ScopeXPath == path && r.Parameter == name); parameterRows.Add(new(path, name, value.Text));
        ContextChanged(); return Task.CompletedTask;
    }
    private Task ResolveAsync()
    {
        if (display.SelectedItem is not ReviewProjectChoice choice) throw new InvalidOperationException("Register/select an ME or SE display XML first.");
        request = new(choice.Project.CurrentSha256, shortcutRows.Select(r => r.Binding).ToArray(), parameterRows.ToArray(),
            string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim());
        return LoadAsync(choice.Project.Id);
    }
    private async Task PageAsync(int direction)
    {
        if (request is null || report is null || direction > 0 && report.Offset + report.References.Length >= report.Total) return;
        request = request with { Offset = Math.Max(0, request.Offset + direction * request.Limit) }; await LoadAsync(report.Project.Id);
    }
    private async Task LoadAsync(string id) => ShowReport(await client.PostAsync<GraphicsResolutionReport>($"/api/projects/{id}/graphics-reference-resolution", request!));
    private void ShowReport(GraphicsResolutionReport result)
    {
        report = result;
        references.DataSource = result.References.Select(r => new { r.Status, Object = r.Source.ObjectName, Original = r.Source.Reference,
            Expanded = r.ExpandedReference, Type = r.DataType + (r.Dimensions is { Length: > 0 } ? "[" + string.Join(',', r.Dimensions) + "]" : "") }).ToArray();
        var previous = scope.Text; scope.Items.Clear(); scope.Items.AddRange(result.ParameterScopes); scope.Text = previous;
        var matches = result.References.Count(r => r.Status == "matched_in_snapshot");
        status.Text = $"Showing {result.References.Length} of {result.Total} matching occurrences (offset {result.Offset}); {matches} matched on this page. {result.Diagnostics.Length} scan diagnostics. Snapshot absence may reflect a partial export; no runtime validation.";
        SelectReference();
    }
    private void SelectReference()
    {
        var index = references.CurrentRow?.Index ?? -1;
        if (report is null || index < 0 || index >= report.References.Length) { details.Clear(); return; }
        var row = report.References[index];
        details.Text = $"Status: {row.Status}\r\nOriginal: {row.Source.Reference}\r\nExpanded: {row.ExpandedReference}\r\nExpression: {row.Source.Expression}\r\n" +
            $"Object scope: {row.Source.ObjectXPath}\r\nSource property: {row.Source.XPath}\r\n" +
            $"PLC project: {row.ProjectId ?? "(unmapped)"}\r\nPLC SHA-256: {row.ProjectSha256 ?? "(unmapped)"}\r\n" +
            $"Program: {row.ProgramName ?? "(controller scope / unresolved)"}  Tag: {row.TagName ?? "(unresolved)"}  Member: {row.MemberPath ?? "(none)"}\r\n" +
            $"Type: {row.DataType ?? "(unresolved)"}  Remaining dimensions: {string.Join(',', row.Dimensions ?? [])}\r\n" +
            "Parameter expansion trace:\r\n  " + (row.ParameterTrace.Length == 0 ? "(none)" : string.Join("\r\n  ", row.ParameterTrace)) +
            "\r\nScan diagnostics:\r\n  " + (report.Diagnostics.Length == 0 ? "(none)" : string.Join("\r\n  ", report.Diagnostics)) +
            (report.DiagnosticsTruncated ? "\r\n  (additional diagnostics omitted)" : "") + "\r\n\r\n" + report.Coverage;
    }
    public void LoadForTest(bool showParameters = false)
    {
        fixture = true;
        var gfx = new ProjectInfo("display-fixture", "Mixer display (generated)", "Generated.xml", "display-hash", "display-hash", 0, DateTimeOffset.UnixEpoch, "ftview-me");
        var logix = gfx with { Id = "plc-fixture", Name = "Saved PLC example", Kind = "logix", CurrentSha256 = "plc-hash" };
        display.Items.Add(new ReviewProjectChoice(gfx)); display.SelectedIndex = 0; plc.Items.Add(new ReviewProjectChoice(logix)); plc.SelectedIndex = 0;
        shortcutRows.Add(new(new("PLC", logix.Id, logix.CurrentSha256), new ReviewProjectChoice(logix).ToString()));
        parameterRows.Add(new("/gfx[1]/group[@name='Mixer']", "#1", "{[PLC]Program:Example.Record}")); ContextChanged();
        var document = XDocument.Parse("<gfx><group name='Mixer'><numericDisplay name='Count' expression='{#1.Count}'/><numericDisplay name='Samples' expression='{[PLC]Program:Example.Record.Samples[2]}'/><numericDisplay name='MissingContext' expression='{[Other]Speed}'/></group></gfx>");
        var plcDocument = XDocument.Parse("<RSLogix5000Content><Controller Name='Generated'><DataTypes><DataType Name='Record'><Members><Member Name='Count' DataType='DINT'/><Member Name='Samples' DataType='INT' Dimension='3'/></Members></DataType></DataTypes><Programs><Program Name='Example'><Tags><Tag Name='Record' DataType='Record'/></Tags></Program></Programs></Controller></RSLogix5000Content>");
        request = new(gfx.CurrentSha256, shortcutRows.Select(r => r.Binding).ToArray(), parameterRows.ToArray());
        ShowReport(GraphicsReferenceResolution.Resolve(gfx, document, request, new Dictionary<string, GraphicsPlcSnapshot> { ["PLC"] = new(logix, plcDocument) }));
        context.SelectedIndex = showParameters ? 1 : 0;
    }
}
