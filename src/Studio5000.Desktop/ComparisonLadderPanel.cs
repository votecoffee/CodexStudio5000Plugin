using System.Xml.Linq;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class ComparisonLadderPanel : UserControl
{
    private readonly ServiceClient client;
    private readonly TreeView routines = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox filter = new() { Width = 190, PlaceholderText = "Program/AOI/routine filter" };
    private readonly CheckBox changed = new() { Text = "Changed rungs only", AutoSize = true };
    private readonly LadderForm.LadderCanvas left = new() { Dock = DockStyle.Fill }, right = new() { Dock = DockStyle.Fill };
    private readonly TextBox leftText = SourceBox(), rightText = SourceBox();
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 45, Padding = new(6) };
    private ProjectCompareRequest? projects;
    private ComparedRoutine? selected;
    private LadderComparisonReport? report;
    private int routineOffset, rungOffset;
    private bool busy, suppress;
    public ComparisonLadderPanel(ServiceClient client)
    {
        this.client = client; Dock = DockStyle.Fill;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, Padding = new(4), AutoScroll = true };
        top.Controls.Add(filter); Button(top, "Find routines", () => { routineOffset = 0; return LoadTreeAsync(); });
        Button(top, "Previous routines", () => RoutinePageAsync(-1)); Button(top, "Next routines", () => RoutinePageAsync(1)); top.Controls.Add(changed);
        Button(top, "Previous rung", () => RungPageAsync(-1)); Button(top, "Next rung", () => RungPageAsync(1));
        changed.CheckedChanged += async (_, _) => await Run(async () => { rungOffset = 0; await LoadRungAsync(); });
        routines.AfterSelect += async (_, _) => { if (!suppress) await Run(async () => { selected = routines.SelectedNode?.Tag as ComparedRoutine; rungOffset = 0; await LoadRungAsync(); }); };
        var navigation = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 230 };
        navigation.Panel1.Controls.Add(routines); var sides = new SplitContainer { Dock = DockStyle.Fill }; sides.Panel1.Controls.Add(Side("LEFT", left, leftText)); sides.Panel2.Controls.Add(Side("RIGHT", right, rightText)); navigation.Panel2.Controls.Add(sides);
        Controls.Add(navigation); Controls.Add(status); Controls.Add(top);
        Resize += (_, _) =>
        {
            if (navigation.Width > 600) navigation.SplitterDistance = 240;
            if (sides.Width > 300) sides.SplitterDistance = sides.Width / 2;
        };
    }
    private static TextBox SourceBox() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private static Control Side(string title, Control canvas, Control text)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal }; split.Panel1.Controls.Add(canvas); split.Panel2.Controls.Add(text);
        var panel = new Panel { Dock = DockStyle.Fill }; panel.Controls.Add(split); panel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, Text = title + " / saved ladder" });
        panel.Resize += (_, _) => { if (split.Height > 160) split.SplitterDistance = split.Height * 2 / 3; }; return panel;
    }
    private void Button(Control parent, string text, Func<Task> action)
    { var button = new Button { Text = text, AutoSize = true }; parent.Controls.Add(button); button.Click += async (_, _) => await Run(action); }
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; Enabled = false;
        try { await action(); } catch (Exception ex) { ClearRungs(); status.Text = ex.Message; }
        finally { busy = false; Enabled = true; }
    }
    public void Clear() { projects = null; selected = null; report = null; suppress = true; routines.Nodes.Clear(); suppress = false; ClearRungs(); status.Text = "Select two snapshots and Compare."; }
    private void ClearRungs() { left.Set(null); right.Set(null); leftText.Clear(); rightText.Clear(); }
    public async Task LoadProjectsAsync(ProjectCompareRequest request)
    {
        Clear(); projects = request; routineOffset = rungOffset = 0; await Run(LoadTreeAsync);
    }
    private int routineTotal;
    private async Task RoutinePageAsync(int direction)
    { if (direction > 0 && routineOffset + 200 >= routineTotal) return; routineOffset = Math.Max(0, routineOffset + direction * 200); await LoadTreeAsync(); }
    private async Task LoadTreeAsync()
    {
        if (projects is null) return;
        var current = projects;
        var page = await client.PostAsync<ComparedRoutinesReport>("/api/projects/compare/routines", current with { Query = string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Trim(), Offset = routineOffset, Limit = 200 });
        if (projects != current) return;
        routineTotal = page.Total; suppress = true; routines.Nodes.Clear(); selected = null; ClearRungs();
        foreach (var group in page.Routines.GroupBy(r => r.Owner))
        {
            var owner = routines.Nodes.Add(group.Key);
            foreach (var r in group)
            {
                var node = owner.Nodes.Add($"{r.Name} · {r.Change} · {r.LeftRungs}/{r.RightRungs}"); node.Tag = r;
                node.ForeColor = r.Change == "unchanged" ? Color.DimGray : r.Change == "added" ? Color.DarkGreen : r.Change == "removed" ? Color.DarkRed : Color.DarkBlue;
            }
        }
        routines.ExpandAll(); suppress = false;
        var first = routines.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>()).FirstOrDefault(n => n.Tag is ComparedRoutine r && (r.LeftType ?? "RLL") == "RLL" && (r.RightType ?? "RLL") == "RLL");
        if (first is not null) { suppress = true; routines.SelectedNode = first; suppress = false; selected = (ComparedRoutine)first.Tag!; rungOffset = 0; await LoadRungAsync(); }
        else status.Text = $"{page.Total} routine matches. Select an RLL routine; other languages remain in the XML comparison.";
    }
    private async Task RungPageAsync(int direction)
    { if (report is null || direction > 0 && rungOffset + 1 >= report.Total) return; rungOffset = Math.Max(0, rungOffset + direction); await LoadRungAsync(); }
    private async Task LoadRungAsync()
    {
        if (projects is null || selected is null) return;
        ClearRungs(); report = null;
        if ((selected.LeftType ?? "RLL") != "RLL" || (selected.RightType ?? "RLL") != "RLL") { status.Text = "Select an RLL pair. Non-ladder routines are compared in the XML view."; return; }
        var current = projects; var routine = selected;
        var value = await client.PostAsync<LadderComparisonReport>("/api/projects/compare/ladder", new LadderCompareRequest(current.LeftProjectId, current.LeftSha256, current.RightProjectId, current.RightSha256,
            routine.LeftXPath, routine.RightXPath, rungOffset, 1, current.IncludeRungNumbers, changed.Checked));
        if (projects == current && selected == routine) Show(value);
    }
    private void Show(LadderComparisonReport value)
    {
        report = value; var rung = value.Rungs.FirstOrDefault(); left.Set(rung?.Left?.Diagram); right.Set(rung?.Right?.Diagram);
        static string Text(LadderRungPreview? r) => r is null ? "(No rung on this side)" : $"Source position {r.Index} · rung {r.Number ?? "unnumbered"} · {r.Status}\r\n{r.Comment}\r\n{r.RawText}\r\n" + string.Join("\r\n", r.Diagnostics) + (r.RawTruncated || r.CommentTruncated ? "\r\nDisplay truncated; inspect/export source XML." : "");
        leftText.Text = Text(rung?.Left); rightText.Text = Text(rung?.Right);
        status.Text = rung is null ? "No matching rungs. Empty routines and unchanged-only filters can have no rows." : $"{selected?.Owner}/{selected?.Name} · aligned row {rung.Position + 1} · {rung.Change} · page {value.Offset + 1}/{value.Total}. Saved structure; no live power-flow or simulation.";
    }
    public void LoadForTest()
    {
        const string xml = "<Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Text>XIC(Enable)[XIC(Auto),XIC(Manual)]ADD(Input,1,Result);</Text></Rung></RLLContent></Routine>";
        var a = new ProjectInfo("left", "Baseline", "Generated.L5X", "a", "a", 0, DateTimeOffset.UnixEpoch); var b = a with { Id = "right", CurrentSha256 = "b" };
        selected = new("Program:Example", "Main", "modified", "RLL", "RLL", "//Routine", "//Routine", 1, 1);
        suppress = true; var parent = routines.Nodes.Add(selected.Owner); parent.Nodes.Add("Main · modified · 1/1").Tag = selected; parent.Expand(); suppress = false;
        Show(LadderComparison.Compare(a, XElement.Parse(xml), b, XElement.Parse(xml.Replace("ADD(Input,1,Result)", "ADD(Input,2,Result)")), new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, "//Routine", "//Routine")));
    }
}
