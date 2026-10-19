using System.Drawing.Drawing2D;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class LadderForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly TagReferenceOccurrence? initialReference;
    private readonly ComboBox projects = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox routines = new() { Width = 410, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox filter = new() { Width = 190, PlaceholderText = "Routine name filter" };
    private readonly LadderCanvas canvas = new() { Dock = DockStyle.Fill };
    private readonly TextBox source = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Font = new("Consolas", 10) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 52, Padding = new(8), Text = "Read-only ladder structure. No live power-flow or simulation." };
    private int offset;
    private LadderPreviewReport? report;
    private bool busy, fixture;
    private sealed record RoutineChoice(ElementInfo Value)
    { public override string ToString() => (Value.Program ?? "AOI/controller") + " / " + Value.Name; }

    public LadderForm(ServiceClient client, ProjectInfo? initial, TagReferenceOccurrence? initialReference = null)
    {
        this.client = client; this.initial = initial; this.initialReference = initialReference;
        Text = "Ladder preview"; Size = new(1380, 880); MinimumSize = new(1050, 650); Font = new("Segoe UI", 10);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new(8) };
        top.Controls.Add(projects); Button(top, "Refresh snapshots", RefreshAsync); top.Controls.Add(filter); Button(top, "Find routines", FindAsync);
        top.SetFlowBreak(top.Controls[^1], true); top.Controls.Add(routines); Button(top, "Load routine", () => { offset = 0; return LoadAsync(); });
        Button(top, "Previous rung", () => PageAsync(-1)); Button(top, "Next rung", () => PageAsync(1));
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 450 };
        split.Panel1.Controls.Add(canvas); split.Panel2.Controls.Add(source);
        Controls.Add(split); Controls.Add(top); Controls.Add(status);
        projects.SelectionChangeCommitted += async (_, _) => await Run(FindAsync);
        routines.SelectionChangeCommitted += (_, _) => { report = null; offset = 0; canvas.Set(null); source.Clear(); status.Text = "Routine selection changed. Select Load routine."; };
        Shown += async (_, _) => { if (!fixture) await Run(this.initialReference is not null ? LoadReferenceAsync : RefreshAsync); };
    }
    private async Task LoadReferenceAsync()
    {
        if (initial is null || initialReference is not { RoutineType: "RLL", RoutineXPath: not null } reference)
            throw new InvalidOperationException("A saved RLL reference and its exact project hash are required.");
        projects.Items.Add(new ReviewProjectChoice(initial)); projects.SelectedIndex = 0;
        routines.Items.Add(new RoutineChoice(new(reference.RoutineXPath, "Routine", reference.OwnerName, reference.ProgramName))); routines.SelectedIndex = 0;
        offset = reference.RungIndex ?? 0; await LoadAsync();
    }
    private void Button(FlowLayoutPanel parent, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true }; parent.Controls.Add(button);
        button.Click += async (_, _) => await Run(action);
    }
    private async Task Run(Func<Task> action)
    {
        if (busy || fixture) return; busy = true; projects.Enabled = routines.Enabled = false;
        try { await action(); }
        catch (Exception ex) { status.Text = "Request failed. Refresh snapshots after a stale hash; select an RLL routine."; MessageBox.Show(this, ex.Message, Text); }
        finally { busy = false; projects.Enabled = routines.Enabled = true; }
    }
    private async Task RefreshAsync()
    {
        var choices = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").Select(p => new ReviewProjectChoice(p)).ToArray();
        projects.DataSource = choices;
        if (initial != null) projects.SelectedItem = choices.FirstOrDefault(c => c.Project.Id == initial.Id);
        if (projects.SelectedIndex < 0 && choices.Length > 0) projects.SelectedIndex = 0;
        await FindAsync();
    }
    private async Task FindAsync()
    {
        report = null; canvas.Set(null); source.Clear(); offset = 0;
        if (projects.SelectedItem is not ReviewProjectChoice choice) { status.Text = "Register a Logix project first."; return; }
        var page = await client.GetAsync<Inspection>($"/api/projects/{choice.Project.Id}/elements?kind=Routine&limit=1000&query=" + Uri.EscapeDataString(filter.Text.Trim()));
        routines.DataSource = page.Elements.Select(e => new RoutineChoice(e)).ToArray();
        status.Text = $"Showing {page.Elements.Count} of {page.Total} routines. Filter for larger projects, select an RLL routine, then Load routine.";
    }
    private async Task PageAsync(int direction)
    {
        if (report is null) return;
        if (direction > 0 && offset + 1 >= report.Total) return;
        offset = Math.Max(0, offset + direction); await LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (projects.SelectedItem is not ReviewProjectChoice choice || routines.SelectedItem is not RoutineChoice routine)
            throw new InvalidOperationException("Select a project and RLL routine first.");
        ShowReport(await client.PostAsync<LadderPreviewReport>($"/api/projects/{choice.Project.Id}/ladder-preview", new LadderPreviewRequest(choice.Project.CurrentSha256, routine.Value.XPath, offset, 1)));
    }
    private void ShowReport(LadderPreviewReport value)
    {
        report = value; var rung = value.Rungs.FirstOrDefault(); canvas.Set(rung?.Diagram);
        source.Text = rung is null ? "No rungs in this RLL routine." : $"Rung {rung.Number ?? rung.Index.ToString()} · {rung.OnlineEditType ?? "saved"} · {rung.Status}\r\n{rung.Comment}\r\n\r\n{rung.RawText}";
        if (rung != null && rung.Diagnostics.Length > 0) source.AppendText("\r\n\r\n" + string.Join("\r\n", rung.Diagnostics));
        if (rung?.RawTruncated == true || rung?.CommentTruncated == true) source.AppendText("\r\nDisplay text is truncated; export the full managed XML.");
        status.Text = $"Rung position {value.Offset + 1} of {value.Total} · {rung?.Status ?? "empty"}. Structural preview only; operands are not evaluated. No live power-flow or native validation.";
    }
    public void LoadForTest()
    {
        fixture = true;
        var project = new ProjectInfo("generated-fixture", "Generated branch example", "Generated.L5X", "hash", "hash", 0, DateTimeOffset.UnixEpoch);
        projects.Items.Add(new ReviewProjectChoice(project)); projects.SelectedIndex = 0;
        routines.Items.Add("ExampleProgram / MainRoutine"); routines.SelectedIndex = 0;
        const string text = "XIC(Enable)[XIC(Auto),[XIC(Manual),XIC(Recovery)]XIO(Fault)]ADD(Input,1,Result)OTE(Output);";
        ShowReport(new(project, "//Routine", 1, 0, 1,
            [new(0, "0", null, text, false, "Generated display fixture; no controller connection.", false, "rendered", [], LadderPreview.Render(text))], "Structural preview only"));
    }

    internal sealed class LadderCanvas : ScrollableControl
    {
        private LadderDiagram? diagram;
        public LadderCanvas() { DoubleBuffered = true; AutoScroll = true; BackColor = Color.White; }
        public void Set(LadderDiagram? value)
        { diagram = value; AutoScrollPosition = Point.Empty; AutoScrollMinSize = value is null ? Size.Empty : new(value.Width, value.Height); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics;
            if (diagram is null) { g.DrawString("No diagram loaded. Raw text and diagnostics remain below.", Font, Brushes.DimGray, 20, 20); return; }
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y); g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(67, 81, 101), 2); using var fill = new SolidBrush(Color.FromArgb(243, 246, 250));
            using var font = new Font("Consolas", 10); using var format = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            foreach (var wire in diagram.Wires) g.DrawLine(pen, wire.X1, wire.Y1, wire.X2, wire.Y2);
            foreach (var node in diagram.Nodes)
            {
                var box = new Rectangle(node.X, node.Y, node.Width, node.Height); g.FillRectangle(fill, box); g.DrawRectangle(pen, box);
                var symbol = node.Kind switch { "contact" => "—| |—", "contact_not" => "—|/|—", "coil" => "—( )—", "coil_latch" => "—(L)—", "coil_unlatch" => "—(U)—", _ => node.Instruction };
                g.DrawString(node.Instruction, font, Brushes.Black, new RectangleF(node.X + 2, node.Y + 3, node.Width - 4, 22), format);
                g.DrawString(symbol, font, Brushes.Black, new RectangleF(node.X + 2, node.Y + 25, node.Width - 4, 22), format);
                g.DrawString(string.Join(", ", node.Operands), font, Brushes.Black, new RectangleF(node.X + 2, node.Y + 48, node.Width - 4, 22), format);
            }
        }
    }
}
