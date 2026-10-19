using System.Xml.Linq;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

internal sealed record ReviewProjectChoice(ProjectInfo Project)
{
    public override string ToString() => $"{Project.Name} · r{Project.Revision} · {Project.Id[..Math.Min(8, Project.Id.Length)]}";
}

public sealed class ProjectCompareForm : Form
{
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly ComboBox left = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox right = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox query = new() { Width = 240, PlaceholderText = "Filter component path or kind" };
    private readonly CheckBox numbers = new() { Text = "Include rung numbers", AutoSize = true };
    private readonly DataGridView changes = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TextBox before = XmlBox(), after = XmlBox();
    private readonly ComparisonLadderPanel ladder;
    private readonly TabControl previews = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 52, Padding = new(8), Text = "Choose two registered Logix snapshots. Structural comparison only." };
    private ProjectComparisonReport? report;
    private ProjectCompareRequest? request;
    private bool busy, fixture;

    public ProjectCompareForm(ServiceClient client, ProjectInfo? initial)
    {
        this.client = client; this.initial = initial; ladder = new(client);
        Text = "Logix project comparison"; Size = new(1380, 880); MinimumSize = new(1100, 700); Font = new("Segoe UI", 10);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new(8), AutoScroll = true };
        top.Controls.Add(new Label { Text = "Left", AutoSize = true, Padding = new(0, 7, 0, 0) }); top.Controls.Add(left);
        top.Controls.Add(new Label { Text = "Right", AutoSize = true, Padding = new(0, 7, 0, 0) }); top.Controls.Add(right);
        Button(top, "Refresh snapshots", RefreshAsync); Button(top, "Compare", CompareAsync);
        Button(top, "Open left…", () => OpenSnapshotAsync(left)); Button(top, "Open right…", () => OpenSnapshotAsync(right));
        top.SetFlowBreak(top.Controls[^1], true); top.Controls.Add(query); top.Controls.Add(numbers);
        Button(top, "Previous", () => PageAsync(-1)); Button(top, "Next", () => PageAsync(1));
        changes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Change", HeaderText = "Change", Width = 100 });
        changes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Kind", HeaderText = "Component", Width = 150 });
        changes.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Path", HeaderText = "Component path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        changes.SelectionChanged += (_, _) => SelectDifference();
        left.SelectionChangeCommitted += (_, _) => ClearSelection(); right.SelectionChangeCommitted += (_, _) => ClearSelection();
        var horizontal = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260 };
        horizontal.Panel1.Controls.Add(changes);
        var sides = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 650 };
        sides.Panel1.Controls.Add(before); sides.Panel2.Controls.Add(after);
        sides.Panel1.Controls.Add(new Label { Dock = DockStyle.Top, Height = 28, Text = "LEFT / normalized XML snippet" });
        sides.Panel2.Controls.Add(new Label { Dock = DockStyle.Top, Height = 28, Text = "RIGHT / normalized XML snippet" });
        var xmlTab = new TabPage("XML differences"); xmlTab.Controls.Add(sides);
        var ladderTab = new TabPage("Ladder comparison browser"); ladderTab.Controls.Add(ladder); previews.TabPages.AddRange([xmlTab, ladderTab]);
        horizontal.Panel2.Controls.Add(previews);
        Controls.Add(horizontal); Controls.Add(top); Controls.Add(status);
        Shown += async (_, _) =>
        {
            horizontal.SplitterDistance = Math.Max(100, horizontal.Height / 4);
            sides.SplitterDistance = Math.Max(100, sides.Width / 2);
            if (!fixture) await Run(RefreshAsync);
        };
    }
    private static TextBox XmlBox() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private void Button(FlowLayoutPanel parent, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true }; parent.Controls.Add(button);
        button.Click += async (_, _) => await Run(action);
    }
    private async Task Run(Func<Task> action)
    {
        if (busy || fixture) return; busy = true; left.Enabled = right.Enabled = false;
        try { await action(); }
        catch (Exception ex) { status.Text = "Request failed; refresh snapshots after a stale hash."; MessageBox.Show(this, ex.Message, Text); }
        finally { busy = false; left.Enabled = right.Enabled = true; }
    }
    private void ClearSelection()
    {
        report = null; request = null; changes.DataSource = null; before.Clear(); after.Clear(); ladder.Clear();
        status.Text = "Snapshot selection changed. Select Compare to load its differences.";
    }
    private async Task RefreshAsync()
    {
        var choices = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").Select(p => new ReviewProjectChoice(p)).ToArray();
        left.DataSource = choices; right.DataSource = choices.ToArray();
        if (initial != null) left.SelectedItem = choices.FirstOrDefault(c => c.Project.Id == initial.Id);
        if (left.SelectedIndex < 0 && choices.Length > 0) left.SelectedIndex = 0;
        if (choices.Length > 1) right.SelectedIndex = left.SelectedIndex == 0 ? 1 : 0;
        report = null; request = null; changes.DataSource = null; before.Clear(); after.Clear(); ladder.Clear();
        status.Text = $"{choices.Length} Logix snapshots. Hashes are pinned when Compare is selected; refresh after other edits.";
    }
    private async Task OpenSnapshotAsync(ComboBox selector)
    {
        using var dialog = new OpenFileDialog { Filter = "Logix projects|*.acd;*.l5x" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(dialog.FileName));
        await RefreshAsync(); selector.SelectedItem = selector.Items.Cast<ReviewProjectChoice>().Single(c => c.Project.Id == project.Id);
    }
    private async Task CompareAsync()
    {
        if (left.SelectedItem is not ReviewProjectChoice a || right.SelectedItem is not ReviewProjectChoice b)
            throw new InvalidOperationException("Register/select two Logix snapshots first.");
        request = new(a.Project.Id, a.Project.CurrentSha256, b.Project.Id, b.Project.CurrentSha256,
            string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim(), IncludeRungNumbers: numbers.Checked);
        await LoadAsync(); await ladder.LoadProjectsAsync(request); previews.SelectedIndex = 1;
    }
    private async Task PageAsync(int direction)
    {
        if (request is null || report is null) return;
        if (direction > 0 && report.Offset + report.Differences.Length >= report.FilteredChanges) return;
        request = request with { Offset = Math.Max(0, request.Offset + direction * request.Limit) }; await LoadAsync();
    }
    private async Task LoadAsync() => ShowReport(await client.PostAsync<ProjectComparisonReport>("/api/projects/compare", request!));
    private void ShowReport(ProjectComparisonReport value)
    {
        report = value; changes.DataSource = value.Differences;
        status.Text = $"{value.TotalChanges} changes: +{value.Added} / −{value.Removed} / ~{value.Modified}. Showing {value.Offset + 1}–{value.Offset + value.Differences.Length} of {value.FilteredChanges} matches. Structural XML only; snippets may be truncated.";
        if (value.FilteredChanges == 0) status.Text = $"{value.TotalChanges} total changes; no differences match this filter. Structural comparison only.";
        SelectDifference();
    }
    private void SelectDifference()
    {
        var difference = changes.CurrentRow?.DataBoundItem as ProjectDifference;
        static string Display(string? text, bool truncated) => text is null ? "(absent)" : truncated ? text : XElement.Parse(text).ToString();
        before.Text = Display(difference?.LeftXml, difference?.LeftTruncated == true); after.Text = Display(difference?.RightXml, difference?.RightTruncated == true);
        if (difference?.LeftTruncated == true) before.AppendText("\r\n… snippet truncated; use the source XPath for inspection.");
        if (difference?.RightTruncated == true) after.AppendText("\r\n… snippet truncated; use the source XPath for inspection.");
    }
    public void LoadForTest(bool showLadder = false)
    {
        fixture = true;
        const string xml = "<RSLogix5000Content><Controller Name='Generated'><Tags><Tag Name='CycleCount' DataType='DINT'><DataValue Value='1'/></Tag></Tags><Programs><Program Name='Example'><Routines><Routine Name='MainRoutine' Type='RLL'><RLLContent><Rung Number='0'><Text>XIC(Enable)OTE(Output);</Text></Rung></RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
        var a = new ProjectInfo("left-fixture", "Baseline example", "Generated.L5X", "hash-a", "hash-a", 0, DateTimeOffset.UnixEpoch);
        var b = a with { Id = "right-fixture", Name = "Edited example", CurrentSha256 = "hash-b", Revision = 1 };
        left.Items.Add(new ReviewProjectChoice(a)); right.Items.Add(new ReviewProjectChoice(b)); left.SelectedIndex = right.SelectedIndex = 0;
        ShowReport(ProjectComparison.Compare(a, XDocument.Parse(xml), b, XDocument.Parse(xml.Replace("Value='1'", "Value='2'").Replace("XIC(Enable)", "XIO(Enable)")), new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256)));
        if (showLadder) { ladder.LoadForTest(); previews.SelectedIndex = 1; }
    }
}
