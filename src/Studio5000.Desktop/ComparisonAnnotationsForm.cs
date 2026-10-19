using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class ComparisonAnnotationsForm : WorkspaceForm
{
    private sealed record StatusChoice(string Code, string Label) { public override string ToString() => Label; }
    private static readonly StatusChoice[] States = [new("unreviewed", "Unreviewed"), new("reviewed", "Reviewed"), new("needs-attention", "Needs attention")];
    private readonly ServiceClient client;
    private ComparisonReviewEntry entry;
    private readonly TextBox query = new() { Width = 280, PlaceholderText = "Search component paths, change kinds or notes" };
    private readonly ComboBox filter = new() { Width = 155, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DataGridView changes = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly ComboBox disposition = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox note = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = ComparisonAnnotations.MaximumNoteLength, AcceptsReturn = true };
    private readonly Label progress = new() { Dock = DockStyle.Top, Height = 38, Padding = new(8) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 56, Padding = new(8), Text = "Local review notes stay tied to this report. Save pending notes before closing." };
    private readonly Dictionary<string, ComparisonAnnotation> drafts = new(StringComparer.OrdinalIgnoreCase);
    private ComparisonAnnotationPage? page;
    private int? draftRevision;
    private bool fixture, busy, binding;
    public ComparisonAnnotationsForm(ServiceClient client, ComparisonReviewEntry entry)
    {
        this.client = client; this.entry = entry; Text = "Comparison change review"; Size = new(1280, 850); MinimumSize = new(1050, 700); Font = new("Segoe UI", 10);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(8) }; top.Controls.Add(query);
        filter.Items.Add(new StatusChoice("all", "All changes")); filter.Items.AddRange(States); filter.SelectedIndex = 0; top.Controls.Add(filter);
        Button(top, "Find / refresh", () => LoadAsync()); Button(top, "Previous", () => PageAsync(-1)); Button(top, "Next", () => PageAsync(1));
        changes.Columns.Add("Number", "#"); changes.Columns[0].Width = 55; changes.Columns.Add("Status", "Review state"); changes.Columns[1].Width = 155;
        changes.Columns.Add("Kind", "Component"); changes.Columns[2].Width = 110; changes.Columns.Add("Change", "Change"); changes.Columns[3].Width = 100;
        changes.Columns.Add("Path", "Recorded component path"); changes.Columns[4].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        changes.SelectionChanged += (_, _) => { if (!binding) ShowEditor(); };
        disposition.Items.AddRange(States); disposition.SelectedIndex = 0;
        var editor = new Panel { Dock = DockStyle.Fill }; editor.Controls.Add(note);
        var editorTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(5) }; editorTop.Controls.Add(disposition);
        editorTop.Controls.Add(new Label { Text = "Note (up to 2000 characters)", AutoSize = true, Padding = new(0, 7, 0, 0) }); editor.Controls.Add(editorTop);
        var bottom = new SplitContainer { Dock = DockStyle.Fill }; bottom.Panel1.Controls.Add(details); bottom.Panel2.Controls.Add(editor);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal }; split.Panel1.Controls.Add(changes); split.Panel2.Controls.Add(bottom);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(8) };
        Button(actions, "Save pending notes", SaveAsync); Button(actions, "Discard pending / reload", DiscardAsync); Button(actions, "Export notes JSON…", ExportAsync);
        Controls.Add(split); Controls.Add(actions); Controls.Add(progress); Controls.Add(top); Controls.Add(status);
        disposition.SelectedIndexChanged += (_, _) => EditDraft(); note.TextChanged += (_, _) => EditDraft();
        Shown += async (_, _) => { split.SplitterDistance = Math.Max(150, split.Height / 2); bottom.SplitterDistance = Math.Max(200, bottom.Width / 2); if (!fixture) await Run(() => LoadAsync()); else if (page is not null) ShowEditor(); };
        FormClosing += (_, e) => { if (drafts.Count != 0) { e.Cancel = true; status.Text = "Save pending notes or use Discard pending / reload before closing."; } };
    }
    private void Button(Control parent, string text, Func<Task> action)
    { var button = new Button { Text = text, AutoSize = true }; parent.Controls.Add(button); button.Click += async (_, _) => await Run(action); }
    private async Task Run(Func<Task> action)
    { if (busy || fixture) return; busy = true; Enabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; Enabled = true; } }
    private AnnotatedComparisonChange? Selected() => changes.CurrentRow?.Tag as AnnotatedComparisonChange;
    private void Progress()
    {
        if (page is null) { progress.Text = "Load a retained comparison report."; return; }
        var s = page.Summary;
        progress.Text = $"{s.Reviewed} reviewed · {s.NeedsAttention} need attention · {s.Unreviewed} unreviewed · {drafts.Count} pending changes · revision {s.Revision}. {page.Matching} matches.";
    }
    private void ShowEditor()
    {
        binding = true;
        try
        {
            var selected = Selected(); note.Enabled = disposition.Enabled = selected is not null;
            if (selected is null) { details.Clear(); note.Clear(); return; }
            var c = selected.Change; var value = drafts.GetValueOrDefault(c.Id) ?? new(c.Id, selected.Status, selected.Note);
            disposition.SelectedItem = States.Single(s => s.Code == value.Status); note.Text = value.Note;
            details.Text = string.Join(Environment.NewLine, $"Recorded change {c.Ordinal + 1}: {c.Change} · {c.Kind}", c.Path, "", "LEFT", c.LeftXPath ?? "(absent)", c.LeftSha256 ?? "(absent)",
                "", "RIGHT", c.RightXPath ?? "(absent)", c.RightSha256 ?? "(absent)", "", "Report SHA-256", entry.Sha256);
        }
        finally { binding = false; }
    }
    private void EditDraft()
    {
        if (binding || page is null || Selected() is not { } selected || disposition.SelectedItem is not StatusChoice state) return;
        var id = selected.Change.Id;
        if (state.Code == selected.Status && note.Text == selected.Note) drafts.Remove(id);
        else
        {
            if (!drafts.ContainsKey(id) && drafts.Count >= ComparisonAnnotations.MaximumBatch)
            { status.Text = "Save or discard the 100 pending changes before editing another one."; ShowEditor(); return; }
            draftRevision ??= page.Summary.Revision; drafts[id] = new(id, state.Code, note.Text);
        }
        if (drafts.Count == 0) draftRevision = null;
        changes.CurrentRow!.Cells[1].Value = States.Single(s => s.Code == (drafts.GetValueOrDefault(id)?.Status ?? selected.Status)).Label + (drafts.ContainsKey(id) ? " *" : "");
        Progress(); status.Text = drafts.Count == 0 ? "All displayed notes match the saved report review." : $"{drafts.Count} pending changes retained across pages and filters. Save uses loaded revision {draftRevision}.";
    }
    private void ShowPage(ComparisonAnnotationPage value, string? selectedId = null)
    {
        page = value; Text = $"Comparison change review · {entry.Left.Name} → {entry.Right.Name}"; binding = true;
        try
        {
            changes.Rows.Clear();
            foreach (var row in value.Changes)
            {
                var state = drafts.GetValueOrDefault(row.Change.Id)?.Status ?? row.Status;
                var index = changes.Rows.Add(row.Change.Ordinal + 1, States.Single(s => s.Code == state).Label + (drafts.ContainsKey(row.Change.Id) ? " *" : ""), row.Change.Kind, row.Change.Change, row.Change.Path);
                changes.Rows[index].Tag = row;
                if (row.Change.Id == selectedId) changes.CurrentCell = changes.Rows[index].Cells[0];
            }
        }
        finally { binding = false; }
        ShowEditor(); Progress();
    }
    private async Task LoadAsync(int offset = 0, string? selectedId = null)
    {
        var url = $"/api/comparison-reviews/{entry.Id}/annotations?offset={offset}&limit=50&query={Uri.EscapeDataString(query.Text.Trim())}&status={((StatusChoice)filter.SelectedItem!).Code}";
        var result = await client.GetAsync<ComparisonAnnotationPage>(url);
        if (!result.Summary.ReportSha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Report identity changed; reopen it from history.");
        ShowPage(result, selectedId);
        if (result.Changes.Length == 0 && result.Matching > 0 && offset > 0) await LoadAsync(Math.Max(0, (result.Matching - 1) / 50 * 50), selectedId);
    }
    private async Task PageAsync(int direction)
    { if (page is null || direction > 0 && page.Offset + page.Changes.Length >= page.Matching) return; await LoadAsync(Math.Max(0, page.Offset + direction * page.Limit)); }
    private async Task SaveAsync()
    {
        if (page is null || drafts.Count == 0 || draftRevision is null) throw new InvalidOperationException("Edit a change's status or note first.");
        var selectedId = Selected()?.Change.Id;
        await client.PostAsync<ComparisonAnnotationSummary>($"/api/comparison-reviews/{entry.Id}/annotations", new ComparisonAnnotationUpdateRequest(entry.Sha256, draftRevision.Value, drafts.Values.ToArray()));
        drafts.Clear(); draftRevision = null; await LoadAsync(page.Offset, selectedId); status.Text = "Saved notes and review states for the recorded report.";
    }
    private async Task DiscardAsync() { drafts.Clear(); draftRevision = null; await LoadAsync(page?.Offset ?? 0); status.Text = "Pending notes discarded; current saved review loaded."; }
    private async Task ExportAsync()
    {
        if (drafts.Count != 0 || page is null) throw new InvalidOperationException("Save or discard pending notes before exporting.");
        using var dialog = new SaveFileDialog { Filter = "Comparison notes|*.json", FileName = "comparison-notes.json", OverwritePrompt = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) { await ExportToAsync(dialog.FileName); status.Text = "Saved notes and progress for the exact report and review revision."; }
    }
    private async Task<Artifact> ExportToAsync(string path)
    {
        if (drafts.Count != 0 || page is null) throw new InvalidOperationException("Save or discard pending notes before exporting.");
        var artifact = await client.PostAsync<Artifact>($"/api/comparison-reviews/{entry.Id}/annotations/export", new ComparisonAnnotationExportRequest(entry.Sha256, page.Summary.Revision));
        await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(path)); return artifact;
    }
    public void LoadForTest(bool workflow = false)
    {
        fixture = true; if (workflow) return;
        var rows = Enumerable.Range(0, 3).Select(i => new AnnotatedComparisonChange(new(new string((char)('a' + i), 64), i, "Rung", $"Program:Example/Main/Rung[{i}]", "modified", "/Left/Rung[1]", "/Right/Rung[1]", new string('a', 64), new string('b', 64)),
            i == 0 ? "reviewed" : i == 1 ? "needs-attention" : "unreviewed", i == 1 ? "Check the expected range before using this change." : "")).ToArray();
        ShowPage(new(new(entry.Id, entry.Sha256, 2, DateTimeOffset.UnixEpoch, 3, 1, 1, 1, ComparisonAnnotations.Boundary), 3, 0, 50, rows));
        changes.CurrentCell = changes.Rows[1].Cells[0];
    }
    public string[] ExerciseOfflineWorkflowForTest(string root)
    {
        if (!fixture) throw new InvalidOperationException("Generated off-screen validation only.");
        var checks = new List<string>(); void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        async Task ExerciseAsync()
        {
            var tags = new XElement("Tags", Enumerable.Range(0, 110).Select(i => new XElement("Tag", new XAttribute("Name", "Value" + i.ToString("D4")), new XAttribute("DataType", "DINT"), new XElement("DataValue", new XAttribute("Value", "1")))));
            var doc = new XDocument(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Generated"), tags)));
            var aPath = Path.Combine(root, "examples", "GuiAnnotationLeft.l5x"); var bPath = Path.Combine(root, "examples", "GuiAnnotationRight.l5x"); doc.Save(aPath);
            foreach (var value in doc.Descendants("DataValue")) value.SetAttributeValue("Value", "2"); doc.Save(bPath);
            var a = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(aPath)); var b = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(bPath));
            entry = (await client.PostAsync<ComparisonReviewExport>("/api/comparison-reviews/export", new ComparisonReviewExportRequest(new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256)))).Review;
            await LoadAsync(); var firstId = Selected()!.Change.Id; disposition.SelectedItem = States[1]; note.Text = "First page checked.";
            await PageAsync(1); disposition.SelectedItem = States[2]; note.Text = "Check this range."; await PageAsync(-1);
            Check(drafts.Count == 2 && note.Text == "First page checked.", "GUI preserves pending annotation drafts across comparison pages");
            await SaveAsync(); Check(page!.Summary is { Revision: 1, Reviewed: 1, NeedsAttention: 1, Unreviewed: 108 }, "GUI atomically saves pending notes and refreshes report progress through the shared service");
            filter.SelectedItem = States[2]; query.Text = "range"; await LoadAsync(); Check(page!.Matching == 1 && note.Text == "Check this range.", "GUI filters review states and searches saved note text");
            filter.SelectedIndex = 0; query.Clear(); await LoadAsync(); note.Text = "Unsaved local edit";
            await client.PostAsync<ComparisonAnnotationSummary>($"/api/comparison-reviews/{entry.Id}/annotations", new ComparisonAnnotationUpdateRequest(entry.Sha256, 1, [new(firstId, "reviewed", "Another client saved this note.")]));
            await LoadAsync(); var rejected = false;
            try { await SaveAsync(); } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { rejected = true; }
            Check(rejected && drafts.Count == 1 && note.Text == "Unsaved local edit", "Refreshing annotations cannot make an older draft overwrite another client's changes; conflict preserves the draft");
            await DiscardAsync(); Check(drafts.Count == 0 && note.Text == "Another client saved this note.", "GUI explicitly discards a conflicting draft and reloads the saved note");
            var path = Path.Combine(root, "artifacts", "exports", "gui-comparison-notes.json"); var artifact = await ExportToAsync(path);
            using var notes = JsonDocument.Parse(File.ReadAllBytes(path));
            Check(notes.RootElement.GetProperty("summary").GetProperty("revision").GetInt32() == 2 && artifact.Sha256 == ProjectService.Hash(File.ReadAllBytes(path)), "GUI exports a hash-verified notes snapshot at the loaded report revision");
        }
        WorkflowValidation.Run(ExerciseAsync); return checks.ToArray();
    }
}
