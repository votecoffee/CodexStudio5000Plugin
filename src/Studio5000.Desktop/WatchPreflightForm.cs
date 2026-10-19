using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class WatchPreflightForm : WorkspaceForm
{
    private readonly DataGridView selections = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TextBox summary = Box(), detail = Box(), checks = Box();
    private WatchPreflightReport? report;

    public WatchPreflightForm(WatchPreflightReport report, string name = "Watch list")
    {
        Text = "Watch preflight — " + name; Size = new(1320, 850); MinimumSize = new(1050, 680); Font = new("Segoe UI", 10);
        summary.Dock = DockStyle.Top; summary.Height = 145; summary.Font = new("Segoe UI", 10); summary.WordWrap = true;
        selections.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Selection", HeaderText = "Tag / member", Width = 340 });
        selections.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Status", HeaderText = "Status", Width = 100 });
        selections.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Type", HeaderText = "Declared type", Width = 140 });
        selections.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Reads", HeaderText = "Scalar reads", Width = 95 });
        selections.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Issue", HeaderText = "Issue", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        selections.SelectionChanged += (_, _) => SelectRow();
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var selectionTab = new TabPage("Selected tag"); selectionTab.Controls.Add(detail);
        var checksTab = new TabPage("Source and configuration"); checksTab.Controls.Add(checks); tabs.TabPages.AddRange([selectionTab, checksTab]);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(selections); split.Panel2.Controls.Add(tabs);
        var footer = new Label { Dock = DockStyle.Bottom, Height = 58, Padding = new(8),
            Text = "Preflight reads local files and configuration only. It does not save or start polling. Save and Start repeat their own checks; live availability and performance remain unverified." };
        Controls.Add(split); Controls.Add(footer); Controls.Add(summary);
        Shown += (_, _) => split.SplitterDistance = Math.Max(180, split.Height / 2);
        LoadReport(report);
    }
    private static TextBox Box() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Font = new("Consolas", 10) };
    private static string Display(WatchTag tag) => (tag.ProgramName is null ? "Controller" : "Program:" + tag.ProgramName) + "." + tag.TagName +
        (string.IsNullOrEmpty(tag.MemberPath) ? "" : tag.MemberPath.StartsWith('[') ? tag.MemberPath : "." + tag.MemberPath);
    private void LoadReport(WatchPreflightReport value)
    {
        report = value; var budget = value.Budget;
        summary.Text = $"{value.Status.ToUpperInvariant()}   |   {value.Mode} / {value.Backend}   |   {value.Selections.Length} selections\r\n" +
            $"Observed source SHA-256: {value.SourceSha256}\r\nExpected source hash: {(value.SourceHashMatches ? "matches" : "CHANGED")}   |   " +
            (value.WatchId is null ? "Unsaved draft / browser selection" : $"Watch {value.WatchId} revision {value.WatchRevision}") + "\r\n" +
            (budget is null ? "SDK read size and native decoding are not estimated." :
                $"{(budget.Complete ? "Complete" : "PARTIAL — supported rows only")}: {budget.UniqueSupportedLeafReads} unique scalar reads, {budget.SupportedPlanNodes} plan nodes, {budget.ScalarValueBytes} scalar value bytes.\r\n" +
                $"Nominal {budget.NominalReadsPerSecond:0.###} scalar reads/s at requested interval; combined limits {(budget.FitsListLimits ? "fit" : "EXCEEDED")}. No throughput measurement.");
        checks.Text = string.Join("\r\n\r\n", value.Checks.Select(c => $"{c.Name}: {c.Status}" + (c.Issue is null ? "" : $"\r\n{c.Issue.Code}: {c.Issue.Message}"))) + "\r\n\r\n" + value.Coverage;
        selections.DataSource = value.Selections.Select(s => new { Selection = Display(s.Tag), s.Status,
            Type = s.DataType + (s.Dimensions.Length == 0 ? "" : "[" + string.Join(',', s.Dimensions) + "]"),
            Reads = s.ScalarReads?.ToString() ?? "—", Issue = string.Join("; ", s.Issues.Select(i => i.Message)) }).ToArray();
        foreach (DataGridViewRow row in selections.Rows)
            row.DefaultCellStyle.ForeColor = value.Selections[row.Index].Status == "blocked" ? Color.DarkRed : value.Selections[row.Index].Status == "deferred" ? Color.DarkGoldenrod : Color.DarkGreen;
        SelectRow();
    }
    private void SelectRow()
    {
        var index = selections.CurrentRow?.Index ?? -1;
        if (report is null || index < 0 || index >= report.Selections.Length) return;
        var row = report.Selections[index];
        detail.Text = $"Key: {row.Tag.Key}\r\nSelection: {Display(row.Tag)}\r\nStatus: {row.Status}\r\nDeclared type: {row.DataType ?? "not inspected"}\r\n" +
            $"Remaining dimensions: {(row.Dimensions.Length == 0 ? "scalar / structure" : string.Join(',', row.Dimensions))}\r\nScalar reads: {row.ScalarReads?.ToString() ?? "not estimated"}\r\n\r\n" +
            (row.Issues.Length == 0 ? "Saved declaration check passed. Live type identity and availability were not checked." :
                string.Join("\r\n\r\n", row.Issues.Select(i => i.Code + ": " + i.Message)));
    }
}
