using System.Xml.Linq;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

internal sealed record ReviewProjectChoice(ProjectInfo Project)
{
    public override string ToString() => $"{Project.Name} · r{Project.Revision} · {Project.Id[..Math.Min(8, Project.Id.Length)]}";
}

public sealed class ProjectCompareForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly ComboBox left = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox right = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox query = new() { Width = 240, PlaceholderText = "Filter component path or kind" };
    private readonly CheckBox numbers = new() { Text = "Include rung numbers", AutoSize = true };
    private readonly ComboBox sessions = new() { Width = 225, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox sessionName = new() { Width = 190, Text = "Comparison session", PlaceholderText = "Session name" };
    private readonly DataGridView changes = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TextBox before = XmlBox(), after = XmlBox();
    private readonly ComparisonLadderPanel ladder;
    private readonly TabControl previews = new() { Dock = DockStyle.Fill };
    private readonly Button locate = new() { Text = "Browse selected change…", AutoSize = true, Enabled = false };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 52, Padding = new(8), Text = "Choose two registered Logix snapshots. Structural comparison only." };
    private ProjectComparisonReport? report;
    private ProjectCompareRequest? request;
    private ComparisonSession? loadedSession;
    private ComparisonSessionRecoveryReport? recovery;
    private bool busy, fixture;

    public ProjectCompareForm(ServiceClient client, ProjectInfo? initial)
    {
        this.client = client; this.initial = initial; ladder = new(client);
        Text = "Logix project comparison"; Size = new(1380, 880); MinimumSize = new(1100, 700); Font = new("Segoe UI", 10);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 180, Padding = new(8), AutoScroll = true };
        top.Controls.Add(new Label { Text = "Left", AutoSize = true, Padding = new(0, 7, 0, 0) }); top.Controls.Add(left);
        top.Controls.Add(new Label { Text = "Right", AutoSize = true, Padding = new(0, 7, 0, 0) }); top.Controls.Add(right);
        Button(top, "Refresh snapshots", RefreshAsync); Button(top, "Compare", CompareAsync);
        Button(top, "Open left…", () => OpenSnapshotAsync(left)); Button(top, "Open right…", () => OpenSnapshotAsync(right));
        top.SetFlowBreak(top.Controls[^1], true); top.Controls.Add(query); top.Controls.Add(numbers);
        Button(top, "Previous", () => PageAsync(-1)); Button(top, "Next", () => PageAsync(1));
        top.Controls.Add(locate); locate.Click += async (_, _) => await Run(LocateAsync);
        top.SetFlowBreak(locate, true); top.Controls.Add(sessions); top.Controls.Add(sessionName);
        Button(top, "Refresh sessions", () => RefreshSessionsAsync()); Button(top, "Load session", LoadSessionAsync);
        Button(top, "Save new", () => SaveSessionAsync(false)); Button(top, "Update selected", () => SaveSessionAsync(true)); Button(top, "Delete selected", DeleteSessionAsync);
        top.SetFlowBreak(top.Controls[^1], true);
        Button(top, "Review recovery", PreviewRecoveryAsync); Button(top, "Save reviewed recovery", ApplyRecoveryAsync);
        top.Controls.Add(new Label { Text = "Recovery uses selected snapshots and clears the previous ladder bookmark.", AutoSize = true, Padding = new(0, 7, 0, 0) });
        query.TextChanged += (_, _) => recovery = null; numbers.CheckedChanged += (_, _) => recovery = null;
        sessions.SelectionChangeCommitted += (_, _) => { if (sessions.SelectedItem is SavedReviewChoice s) sessionName.Text = s.Summary.Name; };
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
            if (!fixture) await Run(async () => { await RefreshAsync(); await RefreshSessionsAsync(); });
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
        recovery = null; report = null; request = null; changes.DataSource = null; before.Clear(); after.Clear(); ladder.Clear();
        status.Text = "Snapshot selection changed. Select Compare to load its differences.";
    }
    private async Task RefreshAsync()
    {
        var choices = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").Select(p => new ReviewProjectChoice(p)).ToArray();
        left.DataSource = choices; right.DataSource = choices.ToArray();
        if (initial != null) left.SelectedItem = choices.FirstOrDefault(c => c.Project.Id == initial.Id);
        if (left.SelectedIndex < 0 && choices.Length > 0) left.SelectedIndex = 0;
        if (choices.Length > 1) right.SelectedIndex = left.SelectedIndex == 0 ? 1 : 0;
        recovery = null; report = null; request = null; changes.DataSource = null; before.Clear(); after.Clear(); ladder.Clear();
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
        recovery = null; request = SelectedComparison();
        await LoadAsync(); await ladder.LoadProjectsAsync(request); previews.SelectedIndex = 1;
    }
    private ProjectCompareRequest SelectedComparison()
    {
        if (left.SelectedItem is not ReviewProjectChoice a || right.SelectedItem is not ReviewProjectChoice b)
            throw new InvalidOperationException("Register/select two Logix snapshots first.");
        return new(a.Project.Id, a.Project.CurrentSha256, b.Project.Id, b.Project.CurrentSha256,
            string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim(), IncludeRungNumbers: numbers.Checked);
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
        locate.Enabled = report is not null && difference?.Kind is "Rung" or "Routine" or "RLLContent";
        static string Display(string? text, bool truncated) => text is null ? "(absent)" : truncated ? text : XElement.Parse(text).ToString();
        before.Text = Display(difference?.LeftXml, difference?.LeftTruncated == true); after.Text = Display(difference?.RightXml, difference?.RightTruncated == true);
        if (difference?.LeftTruncated == true) before.AppendText("\r\n… snippet truncated; use the source XPath for inspection.");
        if (difference?.RightTruncated == true) after.AppendText("\r\n… snippet truncated; use the source XPath for inspection.");
    }
    private async Task LocateAsync()
    {
        if (request is null || report is null || changes.CurrentRow?.DataBoundItem is not ProjectDifference difference) throw new ArgumentException("Select a saved RLL change first.");
        previews.SelectedIndex = 1; await ladder.OpenDifferenceAsync(request, difference);
    }
    private async Task RefreshSessionsAsync(string? id = null)
    {
        id ??= (sessions.SelectedItem as SavedReviewChoice)?.Summary.Id;
        var choices = (await client.GetAsync<ReviewSummary[]>("/api/comparison-sessions")).Select(s => new SavedReviewChoice(s)).ToArray();
        sessions.DataSource = choices; sessions.SelectedItem = choices.FirstOrDefault(s => s.Summary.Id == id) ?? choices.FirstOrDefault();
    }
    private async Task SaveSessionAsync(bool update)
    {
        if (request is null || report is null) throw new InvalidOperationException("Compare current snapshots before saving a session.");
        if (request.Query != (string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim()) || request.IncludeRungNumbers != numbers.Checked)
            throw new InvalidOperationException("Select Compare to include the changed comparison settings before saving.");
        var existing = update ? (sessions.SelectedItem as SavedReviewChoice)?.Summary ?? throw new InvalidOperationException("Select a saved session to update.") : null;
        if (existing is not null && loadedSession?.Id != existing.Id) throw new InvalidOperationException("Load the selected session before updating it.");
        var saved = await client.PostAsync<ComparisonSession>("/api/comparison-sessions", new ComparisonSessionSaveRequest(sessionName.Text, request, ladder.CaptureBookmark(), existing?.Id, existing is null ? null : loadedSession!.Revision));
        loadedSession = saved;
        await RefreshSessionsAsync(saved.Id); status.Text = $"Saved {saved.Name} revision {saved.Revision}, with pinned snapshot hashes and ladder position.";
    }
    private async Task LoadSessionAsync()
    {
        var chosen = (sessions.SelectedItem as SavedReviewChoice)?.Summary ?? throw new InvalidOperationException("Select a saved session.");
        var view = await client.GetAsync<ComparisonSessionView>("/api/comparison-sessions/" + chosen.Id);
        recovery = null; loadedSession = view.Session;
        await RefreshAsync(); await RefreshSessionsAsync(view.Session.Id); sessionName.Text = view.Session.Name;
        var c = view.Session.Comparison;
        left.SelectedItem = left.Items.Cast<ReviewProjectChoice>().FirstOrDefault(p => p.Project.Id == c.LeftProjectId);
        right.SelectedItem = right.Items.Cast<ReviewProjectChoice>().FirstOrDefault(p => p.Project.Id == c.RightProjectId);
        query.Text = c.Query ?? ""; numbers.Checked = c.IncludeRungNumbers;
        if (!view.CanResume) { status.Text = string.Join(" ", view.Issues); return; }
        request = c; await LoadAsync(); await ladder.RestoreAsync(c, view.Session.Bookmark); previews.SelectedIndex = 1;
        if (view.Session.Bookmark is { } bookmark && ladder.DisplayedReport?.Offset != bookmark.RungOffset)
            throw new InvalidOperationException("Saved ladder position could not be restored; refresh snapshots and inspect the ladder diagnostics.");
    }
    private async Task DeleteSessionAsync()
    {
        var chosen = (sessions.SelectedItem as SavedReviewChoice)?.Summary ?? throw new InvalidOperationException("Select a saved session.");
        await client.PostAsync<object>($"/api/comparison-sessions/{chosen.Id}/delete", new ReviewDeleteRequest(chosen.Revision));
        if (loadedSession?.Id == chosen.Id) loadedSession = null;
        recovery = null;
        await RefreshSessionsAsync(); status.Text = "Saved session deleted. Project snapshots remain available.";
    }
    private ComparisonSession RequireLoadedSession()
    {
        if (loadedSession is null || loadedSession.Id != (sessions.SelectedItem as SavedReviewChoice)?.Summary.Id)
            throw new InvalidOperationException("Load the selected session before reviewing recovery.");
        return loadedSession;
    }
    private async Task PreviewRecoveryAsync()
    {
        recovery = null; var session = RequireLoadedSession(); var selected = SelectedComparison();
        var preview = await client.PostAsync<ComparisonSessionRecoveryReport>("/api/comparison-sessions/recovery/preview",
            new ComparisonSessionRecoveryRequest(session.Id, session.Revision, selected));
        request = preview.Proposed; ShowReport(preview.Comparison); await ladder.LoadProjectsAsync(request); previews.SelectedIndex = 1;
        recovery = preview; status.Text = $"Recovery preview: {preview.Comparison.TotalChanges} current differences. Saving pins these snapshots and clears the previous bookmark. Saved session is unchanged.";
    }
    private async Task ApplyRecoveryAsync()
    {
        var session = RequireLoadedSession(); var preview = recovery ?? throw new InvalidOperationException("Review recovery before saving it.");
        if (preview.Session.Id != session.Id || preview.Session.Revision != session.Revision || SelectedComparison() != preview.Proposed)
            throw new InvalidOperationException("Snapshot selection or filters changed; review recovery again.");
        loadedSession = await client.PostAsync<ComparisonSession>("/api/comparison-sessions/recovery/apply",
            new ComparisonSessionRecoveryApplyRequest(new(session.Id, session.Revision, preview.Proposed), preview.ReviewSha256));
        recovery = null; await RefreshSessionsAsync(loadedSession.Id);
        status.Text = $"Recovered session revision {loadedSession.Revision}. Previous bookmark cleared; choose a rung in the current comparison before saving a new position.";
    }
    public void LoadForTest(bool showLadder = false, bool workflow = false)
    {
        fixture = true; if (workflow) return;
        const string xml = "<RSLogix5000Content><Controller Name='Generated'><Tags><Tag Name='CycleCount' DataType='DINT'><DataValue Value='1'/></Tag></Tags><Programs><Program Name='Example'><Routines><Routine Name='MainRoutine' Type='RLL'><RLLContent><Rung Number='0'><Text>XIC(Enable)OTE(Output);</Text></Rung></RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
        var a = new ProjectInfo("left-fixture", "Baseline example", "Generated.L5X", "hash-a", "hash-a", 0, DateTimeOffset.UnixEpoch);
        var b = a with { Id = "right-fixture", Name = "Edited example", CurrentSha256 = "hash-b", Revision = 1 };
        left.Items.Add(new ReviewProjectChoice(a)); right.Items.Add(new ReviewProjectChoice(b)); left.SelectedIndex = right.SelectedIndex = 0;
        ShowReport(ProjectComparison.Compare(a, XDocument.Parse(xml), b, XDocument.Parse(xml.Replace("Value='1'", "Value='2'").Replace("XIC(Enable)", "XIO(Enable)")), new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256)));
        if (showLadder) { ladder.LoadForTest(); previews.SelectedIndex = 1; }
    }
    public string[] ExerciseOfflineWorkflowForTest(string root)
    {
        if (!fixture) throw new InvalidOperationException("Generated off-screen validation only.");
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        async Task ExerciseAsync()
        {
            var caps = await client.GetAsync<System.Text.Json.JsonElement>("/api/sdk/capabilities"); Check(!caps.GetProperty("controllerOperationsEnabled").GetBoolean(), "GUI comparison workflow requires disabled controller operations");
            var doc = XDocument.Parse("<RSLogix5000Content><Controller Name='Generated'><Programs><Program Name='Example'><Routines><Routine Name='Main' Type='RLL'><RLLContent/></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>");
            doc.Descendants("RLLContent").Single().Add(Enumerable.Range(0, 20).Select(i => new XElement("Rung", new XAttribute("Number", i), new XElement("Text", $"MOV({i},Count);"))));
            var aPath = Path.Combine(root, "examples", "GuiCompareLeft.l5x"); var bPath = Path.Combine(root, "examples", "GuiCompareRight.l5x"); doc.Save(aPath);
            var original = doc.Descendants("Rung").ToArray(); original[17].Element("Text")!.Value = "MOV(999,Count);";
            original[4].AddBeforeSelf(new XElement("Rung", new XAttribute("Number", "4"), new XElement("Text", "NOP();"))); doc.Save(bPath);
            var a = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(aPath)); var b = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(bPath)); await RefreshAsync();
            left.SelectedItem = left.Items.Cast<ReviewProjectChoice>().Single(c => c.Project.Id == a.Id); right.SelectedItem = right.Items.Cast<ReviewProjectChoice>().Single(c => c.Project.Id == b.Id); await CompareAsync();
            Check(report is { TotalChanges: 2 } && ladder.DisplayedReport is { Total: 21 }, "GUI loads the independent comparison and its aligned routine browser");
            async Task Open(string change)
            {
                var row = changes.Rows.Cast<DataGridViewRow>().Single(r => r.DataBoundItem is ProjectDifference d && d.Change == change);
                changes.CurrentCell = row.Cells[0]; Check(locate.Enabled, "GUI enables navigation for the selected " + change + " rung"); await LocateAsync();
            }
            await Open("modified"); Check(ladder.DisplayedReport is { Offset: 18 } page && page.Rungs.Single() is { Left.Index: 17, Right.Index: 18 }, "GUI selected-change action jumps to the late modified source occurrences");
            sessionName.Text = "Generated saved comparison"; await SaveSessionAsync(false); ClearSelection(); await LoadSessionAsync();
            Check(ladder.DisplayedReport is { Offset: 18 } restored && restored.Rungs.Single() is { Left.Index: 17, Right.Index: 18 }, "GUI saves and restores a comparison session at the exact aligned rung");
            await Open("added"); Check(ladder.DisplayedReport!.Rungs.Single() is { Change: "added", Left: null, Right.Index: 4 }, "GUI selected-change action preserves the insertion gap and infers the existing routine");
            ClearSelection(); Check(report is null && ladder.DisplayedReport is null && !locate.Enabled, "Changing GUI snapshot selection clears comparison navigation and disables its action");
            // Replacing the selected right snapshot models recovery to a newly registered revision without modifying either original.
            original[17].Element("Text")!.Value = "MOV(555,Count);";
            var cPath = Path.Combine(root, "examples", "GuiCompareRecovery.l5x"); doc.Save(cPath);
            var c = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(cPath));
            await LoadSessionAsync(); await RefreshAsync();
            left.SelectedItem = left.Items.Cast<ReviewProjectChoice>().Single(p => p.Project.Id == a.Id);
            right.SelectedItem = right.Items.Cast<ReviewProjectChoice>().Single(p => p.Project.Id == c.Id);
            await PreviewRecoveryAsync();
            var unchanged = await client.GetAsync<ComparisonSessionView>("/api/comparison-sessions/" + loadedSession!.Id);
            Check(unchanged.Session.Comparison.RightProjectId == b.Id && unchanged.Session.Bookmark?.RungOffset == 18 && recovery?.Proposed.RightProjectId == c.Id,
                "GUI recovery previews explicitly selected snapshots while retaining the original saved comparison and bookmark");
            numbers.Checked = !numbers.Checked; var rejected = false;
            try { await ApplyRecoveryAsync(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Changing GUI comparison filters invalidates the reviewed recovery");
            numbers.Checked = !numbers.Checked; await PreviewRecoveryAsync(); await ApplyRecoveryAsync();
            Check(loadedSession is { Revision: 1, Bookmark: null } && loadedSession.Comparison.RightProjectId == c.Id,
                "GUI saves recovery through the shared service and clears the previous aligned rung position");
            await LoadSessionAsync(); await Open("modified");
        }
        WorkflowValidation.Run(ExerciseAsync);
        Check(previews.SelectedIndex == 1 && ladder.Visible && ladder.IsHandleCreated && ladder.Width > 0 && ladder.Height > 0, "GUI navigation leaves a visible ladder pane with handles owned by the UI thread");
        return checks.ToArray();
    }
}
