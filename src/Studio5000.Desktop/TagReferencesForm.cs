using System.Xml.Linq;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class TagReferencesForm : WorkspaceForm
{
    private sealed record ScopeChoice(string? Name) { public override string ToString() => Name ?? "(controller tags)"; }
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly string? initialTag, initialProgram;
    private readonly ComboBox projects = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox scopes = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox tag = new() { Width = 180 };
    private readonly TextBox filter = new() { Width = 380, PlaceholderText = "Operand, routine, role or status" };
    private readonly FlowLayoutPanel top = new() { Dock = DockStyle.Top, Height = 128, Padding = new(8) };
    private readonly DataGridView results = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TextBox detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Font = new("Consolas", 10) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 54, Padding = new(8),
        Text = "Select a saved Logix snapshot, exact base tag and scope. Results are lexical candidates for review." };
    private TagReferenceRequest? request;
    private TagReferenceReport? report;
    private bool busy, fixture;

    public TagReferencesForm(ServiceClient client, ProjectInfo? initial, string? initialTag = null, string? initialProgram = null)
    {
        this.client = client; this.initial = initial; this.initialTag = initialTag; this.initialProgram = initialProgram;
        Text = "PLC tag where-used — saved source candidates"; Size = new(1450, 880); MinimumSize = new(1160, 720); Font = new("Segoe UI", 10);
        Caption("Snapshot"); top.Controls.Add(projects); Button("Refresh snapshots", () => RefreshAsync(false));
        Caption("Scope"); top.Controls.Add(scopes); Caption("Base tag"); top.Controls.Add(tag); Button("Find references", FindAsync);
        top.SetFlowBreak(top.Controls[^1], true); Caption("Filter"); top.Controls.Add(filter);
        Button("Previous", () => PageAsync(-1)); Button("Next", () => PageAsync(1)); Button("Open selected ladder rung…", OpenLadderAsync);
        Column("ProgramName", "Source program", 150); Column("OwnerName", "Routine / alias", 180); Column("RoutineType", "Type", 60);
        Column("Number", "Rung / line", 85); Column("Operand", "Source operand", 260, true); Column("Role", "Role", 150); Column("Status", "Status", 220);
        results.Columns[2].DefaultCellStyle.NullValue = "alias";
        results.SelectionChanged += (_, _) => SelectReference();
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(results); split.Panel2.Controls.Add(detail); Controls.Add(split); Controls.Add(top); Controls.Add(status);
        projects.SelectionChangeCommitted += async (_, _) => await Run(LoadScopesAsync);
        scopes.SelectionChangeCommitted += (_, _) => ClearReport(); tag.TextChanged += (_, _) => ClearReport();
        Shown += async (_, _) => { split.SplitterDistance = Math.Max(200, split.Height / 2); if (!fixture) await Run(() => RefreshAsync(true)); };
    }
    private void Caption(string text) => top.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new(0, 6, 0, 0) });
    private void Column(string property, string text, int width, bool fill = false) => results.Columns.Add(new DataGridViewTextBoxColumn
    { DataPropertyName = property, HeaderText = text, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None });
    private void Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true }; top.Controls.Add(button); button.Click += async (_, _) => await Run(action);
    }
    private async Task Run(Func<Task> action)
    {
        if (busy || fixture) return; busy = true; top.Enabled = false;
        try { await action(); }
        catch (Exception ex) { ClearReport(); status.Text = "Request failed. Refresh the snapshot/scopes after a stale hash."; MessageBox.Show(this, ex.Message, Text); }
        finally { busy = false; top.Enabled = true; }
    }
    private void ClearReport() { report = null; request = null; results.DataSource = null; detail.Clear(); status.Text = "Selection changed. Find references to inspect this base tag and scope."; }
    private async Task RefreshAsync(bool opening)
    {
        var selected = (projects.SelectedItem as ReviewProjectChoice)?.Project.Id ?? initial?.Id;
        var choices = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix")
            .Select(p => new ReviewProjectChoice(opening && initial?.Id == p.Id ? initial : p)).ToArray();
        projects.DataSource = choices;
        if (choices.FirstOrDefault(p => p.Project.Id == selected) is { } choice) projects.SelectedItem = choice;
        await LoadScopesAsync();
        if (opening && initialTag is not null)
        {
            scopes.SelectedItem = scopes.Items.Cast<ScopeChoice>().FirstOrDefault(s => string.Equals(s.Name, initialProgram, StringComparison.OrdinalIgnoreCase));
            tag.Text = initialTag; await FindAsync();
        }
    }
    private async Task LoadScopesAsync()
    {
        ClearReport(); scopes.Items.Clear();
        if (projects.SelectedItem is not ReviewProjectChoice choice) { status.Text = "Register a saved Logix project first."; return; }
        var page = await client.PostAsync<TagBrowserPage>("/api/tags/browse", new TagBrowseRequest(choice.Project.Id, choice.Project.CurrentSha256, Limit: 1));
        scopes.Items.Add(new ScopeChoice(null)); foreach (var program in page.Programs) scopes.Items.Add(new ScopeChoice(program)); scopes.SelectedIndex = 0;
        status.Text = "Select an exact base tag/scope. The opening snapshot stays pinned until Refresh snapshots.";
    }
    private Task FindAsync()
    {
        if (projects.SelectedItem is not ReviewProjectChoice choice || scopes.SelectedItem is not ScopeChoice scope)
            throw new InvalidOperationException("Select a saved Logix snapshot and tag scope first.");
        request = new(choice.Project.CurrentSha256, tag.Text.Trim(), scope.Name, string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Trim());
        return LoadAsync(choice.Project.Id);
    }
    private async Task PageAsync(int direction)
    {
        if (request is null || report is null || direction > 0 && report.Offset + report.References.Length >= report.Total) return;
        request = request with { Offset = Math.Max(0, request.Offset + direction * request.Limit) }; await LoadAsync(report.Project.Id);
    }
    private async Task LoadAsync(string id) => ShowReport(await client.PostAsync<TagReferenceReport>($"/api/projects/{id}/tag-references", request!));
    private void ShowReport(TagReferenceReport value)
    {
        report = value; results.DataSource = value.References;
        status.Text = $"{value.ProgramName ?? "Controller"}.{value.TagName}: {value.Total} matching candidates; showing {value.References.Length} at offset {value.Offset}. {value.ScannedSources} scanned / {value.SkippedSources} skipped sources. Lexical matches do not prove compiler binding or that an unlisted tag is unused.";
        SelectReference();
    }
    private void SelectReference()
    {
        if (report is null || results.CurrentRow?.DataBoundItem is not TagReferenceOccurrence row) { detail.Clear(); return; }
        detail.Text = $"{row.Status} / {row.Role}\r\nSource: {row.ProgramName ?? "Controller"} / {row.OwnerName} ({row.RoutineType ?? row.Kind})\r\n" +
            $"Property: {row.SourceXPath}\r\nCharacter offset: {row.Start}; length: {row.Length}\r\nOperand: {row.Operand}\r\n" +
            $"Snapshot SHA-256: {report.Project.CurrentSha256}\r\n\r\nSource snippet (starts at {row.SnippetStart}{(row.SnippetTruncated ? ", truncated" : "")}):\r\n{row.Snippet}\r\n\r\n" +
            "Diagnostics:\r\n" + (report.Diagnostics.Length == 0 ? "(none)" : string.Join("\r\n", report.Diagnostics)) +
            (report.DiagnosticsTruncated ? "\r\n(additional diagnostics omitted)" : "") + "\r\n\r\n" + report.Coverage;
    }
    private Task OpenLadderAsync()
    {
        if (report is null || results.CurrentRow?.DataBoundItem is not TagReferenceOccurrence { RoutineType: "RLL", RoutineXPath: not null } row)
            throw new InvalidOperationException("Select an RLL candidate; ST and alias source is shown in the details pane.");
        using var ladder = new LadderForm(client, report.Project, row); ladder.ShowDialog(this); return Task.CompletedTask;
    }
    public void LoadForTest()
    {
        fixture = true;
        Width = MinimumSize.Width;
        var p = new ProjectInfo("fixture", "Generated references", "Generated.L5X", new string('a', 64), new string('a', 64), 0, DateTimeOffset.UnixEpoch);
        projects.Items.Add(new ReviewProjectChoice(p)); projects.SelectedIndex = 0; scopes.Items.Add(new ScopeChoice("ExampleProgram")); scopes.SelectedIndex = 0; tag.Text = "Input";
        var document = XDocument.Parse("<RSLogix5000Content><Controller Name='Generated'><Programs><Program Name='ExampleProgram'><Tags><Tag Name='Input' DataType='DINT'/><Tag Name='InputAlias' TagType='Alias' AliasFor='Input'/></Tags><Routines><Routine Name='MainRoutine' Type='RLL'><RLLContent><Rung Number='0'><Text>ADD(Input,1,Result);</Text></Rung></RLLContent></Routine><Routine Name='Calculate' Type='ST'><STContent><Line Number='0'>Result := Input + 1; // Input in a comment is skipped.</Line></STContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>");
        ShowReport(TagReferences.Find(p, document, new(p.CurrentSha256, "Input", "ExampleProgram")));
    }
}
