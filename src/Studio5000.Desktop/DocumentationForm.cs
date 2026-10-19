using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class DocumentationForm : WorkspaceForm
{
    private sealed record Row(DocumentationField Field, DocumentationText? Value);
    private static readonly string[] Kinds = ["description", "rung-comment", "tag-comment", "engineering-unit", "state0", "state1", "additional-help"];
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly ComboBox projects = new() { Width = 255, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly TextBox query = new() { Width = 200, PlaceholderText = "Search names / text" };
    private readonly ComboBox kindFilter = new() { Width = 145, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox languageFilter = new() { Width = 105, PlaceholderText = "Any language" };
    private readonly CheckBox missing = new() { Text = "Include missing fields", AutoSize = true, Checked = true };
    private readonly DataGridView fields = new() { Dock = DockStyle.Fill, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly ComboBox editKind = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox language = new() { Width = 125, PlaceholderText = "Empty = unlocalized" };
    private readonly TextBox legacy = new() { Width = 110, PlaceholderText = "Original language" };
    private readonly TextBox operand = new() { Width = 160, PlaceholderText = "Member / bit suffix" };
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, MaxLength = 65536, ScrollBars = ScrollBars.Both, Font = new("Segoe UI", 11) };
    private readonly TextBox result = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly Label selection = new() { Dock = DockStyle.Top, Height = 55, Padding = new(7) };
    private readonly Label metadata = new() { Dock = DockStyle.Top, Height = 58, Padding = new(8) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 50, Padding = new(8) };
    private readonly Button apply = new() { Text = "Apply reviewed comments", AutoSize = true, Enabled = false };
    private readonly Dictionary<string, DocumentationEdit> pending = new(StringComparer.OrdinalIgnoreCase);
    private ProjectInfo? project;
    private DocumentationPage? page;
    private DocumentationPreview? preview;
    private DocumentationPreviewRequest? proposed;
    private int offset;
    private bool busy, loading, fixture;
    public DocumentationForm(ServiceClient client, ProjectInfo? initial = null)
    {
        this.client = client; this.initial = initial;
        Text = "Multilingual project documentation"; Size = new(1450, 950); MinimumSize = new(1150, 800); Font = new("Segoe UI", 10);
        var header = new Label { Text = "PROJECT DOCUMENTATION  /  EXPLICIT LANGUAGES", Dock = DockStyle.Top, Height = 48, Padding = new(12), BackColor = Color.FromArgb(24, 40, 61), ForeColor = Color.White };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new(7) };
        toolbar.Controls.Add(projects); toolbar.Controls.Add(query); kindFilter.Items.AddRange(["all", ..Kinds]); kindFilter.SelectedIndex = 0; toolbar.Controls.Add(kindFilter); toolbar.Controls.Add(languageFilter); toolbar.Controls.Add(missing);
        Button(toolbar, "Search / refresh", async () => { offset = 0; await LoadAsync(); }); Button(toolbar, "Previous", async () => { offset = Math.Max(0, offset - 100); await LoadAsync(); });
        Button(toolbar, "Next", async () => { if (page is not null && offset + 100 < page.Total) { offset += 100; await LoadAsync(); } }); Button(toolbar, "Export text JSON…", ExportAsync);
        foreach (var (name, width) in new[] { ("Owner", 140), ("Kind", 110), ("Component context", 240), ("Operand", 90), ("Language", 90), ("Text", 420), ("Status", 150) }) fields.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = width });
        var editorBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 90, Padding = new(6) };
        editKind.Items.AddRange(Kinds); editKind.SelectedIndex = 0; editorBar.Controls.Add(editKind); editorBar.Controls.Add(language); editorBar.Controls.Add(operand);
        editorBar.Controls.Add(new Label { Text = "Promote plain text from", AutoSize = true, Padding = new(0, 7, 0, 0) }); editorBar.Controls.Add(legacy);
        Button(editorBar, "Stage set", () => { Stage("set"); return Task.CompletedTask; }); Button(editorBar, "Stage remove translation", () => { Stage("remove"); return Task.CompletedTask; });
        var editor = new Panel { Dock = DockStyle.Fill }; editor.Controls.Add(text); editor.Controls.Add(editorBar); editor.Controls.Add(selection);
        var tabs = new TabControl { Dock = DockStyle.Fill }; var reviewTab = new TabPage("Pending edits / reviewed diff"); reviewTab.Controls.Add(result); tabs.TabPages.Add(reviewTab);
        var bottom = new SplitContainer { Dock = DockStyle.Fill }; bottom.Panel1.Controls.Add(editor); bottom.Panel2.Controls.Add(tabs);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal }; split.Panel1.Controls.Add(fields); split.Panel2.Controls.Add(bottom);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(7) };
        Button(actions, "Preview pending edits", PreviewAsync); actions.Controls.Add(apply); Button(actions, "Discard pending / reload", DiscardAsync);
        actions.Controls.Add(new Label { Text = "Blank language = unlocalized. Other languages are retained. No PLC connection.", AutoSize = true, Padding = new(7, 7, 0, 0) });
        Controls.Add(split); Controls.Add(actions); Controls.Add(status); Controls.Add(metadata); Controls.Add(toolbar); Controls.Add(header);
        projects.SelectedIndexChanged += async (_, _) => { if (!loading) await Run(SelectProjectAsync); };
        fields.SelectionChanged += (_, _) => ShowEditor(); apply.Click += async (_, _) => await Run(ApplyAsync);
        Shown += async (_, _) => { split.SplitterDistance = Math.Max(220, split.Height / 2); bottom.SplitterDistance = bottom.Width / 2; if (!fixture) await Run(InitializeAsync); };
        FormClosing += (_, e) => { if (!fixture && pending.Count != 0) { e.Cancel = true; status.Text = "Apply the reviewed edits or Discard pending / reload before closing."; } };
    }
    private void Button(Control parent, string label, Func<Task> action)
    { var button = new Button { Text = label, AutoSize = true }; button.Click += async (_, _) => await Run(action); parent.Controls.Add(button); }
    private async Task Run(Func<Task> action)
    { if (busy) return; busy = true; Enabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; Enabled = true; } }
    private async Task InitializeAsync()
    {
        var values = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").ToArray(); loading = true; projects.DataSource = values;
        projects.SelectedItem = values.FirstOrDefault(p => p.Id == initial?.Id) ?? values.FirstOrDefault(); loading = false; await SelectProjectAsync();
    }
    private async Task SelectProjectAsync()
    {
        if (pending.Count != 0) { loading = true; projects.SelectedItem = ((ProjectInfo[])projects.DataSource!).FirstOrDefault(p => p.Id == project?.Id); loading = false; throw new InvalidOperationException("Save or discard pending documentation edits before switching projects."); }
        project = projects.SelectedItem as ProjectInfo; offset = 0; preview = null; proposed = null; apply.Enabled = false;
        if (project is not null) await LoadAsync();
    }
    private DocumentationQuery Query() => new(project!.CurrentSha256, Empty(query.Text), kindFilter.SelectedIndex <= 0 ? null : (string)kindFilter.SelectedItem!, Empty(languageFilter.Text), offset, 100, missing.Checked);
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private async Task LoadAsync()
    {
        if (project is null) throw new InvalidOperationException("Select a saved Logix project.");
        var latest = await client.GetAsync<ProjectInfo>("/api/projects/" + project.Id);
        if (latest.CurrentSha256 != project.CurrentSha256 && pending.Count != 0) throw new InvalidOperationException("Source changed. Pending edits were preserved; discard/reload before editing the new revision.");
        project = latest; page = await client.PostAsync<DocumentationPage>($"/api/projects/{project.Id}/documentation/browse", Query());
        fields.Rows.Clear();
        foreach (var field in page.Fields)
        foreach (var value in field.Texts.Length == 0 ? new DocumentationText?[] { null } : field.Texts)
        {
            var display = value?.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ↵ ") ?? "(missing)";
            var row = fields.Rows[fields.Rows.Add(field.Name, field.Kind, field.Context, field.Operand, value?.Language ?? "(unlocalized)", display, field.Writable ? field.Exists ? "editable" : "create" : string.Join(", ", field.Diagnostics))];
            row.Tag = new Row(field, value);
        }
        metadata.Text = $"r{project.Revision} • {project.CurrentSha256[..12]} • Current: {page.CurrentLanguage ?? "unassociated"} • Default: {page.DefaultLanguage ?? "unassociated"} • Observed: {string.Join(", ", page.Languages)}\nFields {Math.Min(offset + 1, page.Total)}–{Math.Min(offset + 100, page.Total)} / {page.Total} • {pending.Count} staged language edits retained across pages.";
        ShowEditor();
    }
    private Row? Selected => fields.CurrentRow?.Tag as Row;
    private void ShowEditor()
    {
        var row = Selected; if (row is null) { selection.Text = "Select a documentation field."; text.Clear(); return; }
        editKind.SelectedItem = row.Field.Kind; language.Text = row.Value?.Language ?? ""; operand.Text = row.Field.Operand ?? ""; legacy.Clear(); text.Text = row.Value?.Text ?? "";
        selection.Text = row.Field.OwnerXPath + "\n" + (row.Field.Writable ? "Edit this explicit language or enter a new language code. Stage edits before preview." : string.Join(", ", row.Field.Diagnostics));
    }
    private void Stage(string operation)
    {
        if (Selected is not { } row || !row.Field.Writable) throw new InvalidOperationException("Select an editable documentation field.");
        var edit = new DocumentationEdit(row.Field.OwnerXPath, (string)editKind.SelectedItem!, Empty(language.Text), operation == "set" ? text.Text : null, Empty(operand.Text), operation, Empty(legacy.Text));
        var key = edit.OwnerXPath + "|" + edit.Kind + "|" + edit.Operand + "|" + edit.Language;
        if (!pending.ContainsKey(key) && pending.Count >= 200) throw new InvalidOperationException("Apply or discard the 200 pending edits before adding another.");
        pending[key] = edit; preview = null; proposed = null; apply.Enabled = false; result.Text = JsonSerializer.Serialize(pending.Values, ProjectService.Json); status.Text = $"{pending.Count} staged language edits. Review the preview before applying.";
    }
    private async Task PreviewAsync()
    {
        if (project is null || pending.Count == 0) throw new InvalidOperationException("Stage at least one documentation edit.");
        proposed = new(project.CurrentSha256, pending.Values.ToArray()); preview = null; apply.Enabled = false;
        preview = await client.PostAsync<DocumentationPreview>($"/api/projects/{project.Id}/documentation/preview", proposed); result.Text = JsonSerializer.Serialize(preview, ProjectService.Json); apply.Enabled = true;
        status.Text = $"Review {preview.Changes.Length} explicit language changes against r{project.Revision}. Other languages, logic and values are preserved.";
    }
    private async Task ApplyAsync()
    {
        if (project is null || preview is null || proposed is null) throw new InvalidOperationException("Preview the staged edits first.");
        project = await client.PostAsync<ProjectInfo>($"/api/projects/{project.Id}/documentation/apply", new DocumentationApplyRequest(proposed, preview.ReviewSha256));
        pending.Clear(); proposed = null; preview = null; apply.Enabled = false; await LoadAsync(); status.Text = $"Documentation saved to managed r{project.Revision}. Export a new L5X/ACD when ready.";
    }
    private async Task DiscardAsync() { pending.Clear(); proposed = null; preview = null; apply.Enabled = false; result.Clear(); await LoadAsync(); }
    private async Task<SavedArtifact> ExportFileAsync(string path)
    {
        if (project is null) throw new InvalidOperationException("Select a project.");
        var artifact = await client.PostAsync<Artifact>($"/api/projects/{project.Id}/documentation/export", new DocumentationExportRequest(Query()));
        return await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(path));
    }
    private async Task ExportAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "Multilingual documentation JSON|*.json", FileName = "project-documentation.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return; var saved = await ExportFileAsync(dialog.FileName); status.Text = "Saved all matching fields and translations: " + saved.Path;
    }
    internal async Task<string[]> ExerciseOfflineWorkflowForTestAsync(string root)
    {
        fixture = true; await InitializeAsync();
        void Select(string name, string kind, string locale)
        { var row = fields.Rows.Cast<DataGridViewRow>().Single(r => r.Tag is Row x && x.Field.Name == name && x.Field.Kind == kind && x.Value?.Language == locale); fields.CurrentCell = row.Cells[0]; ShowEditor(); }
        Select("Record", "description", "es-ES"); text.Text = "Producción revisada 零"; Stage("set");
        Select("0", "rung-comment", "en-US"); language.Text = "ja-JP"; text.Text = "出力許可"; Stage("set");
        await PreviewAsync(); if (preview!.Changes.Length != 2 || !apply.Enabled) throw new InvalidOperationException("Documentation editor lost staged changes.");
        await ApplyAsync(); if (project!.Revision != 1) throw new InvalidOperationException("Documentation apply did not create a revision.");
        Select("Record", "description", "es-ES"); if (text.Text != "Producción revisada 零") throw new InvalidOperationException("Unicode edit was not read back."); text.Text = "Pending local text"; Stage("set");
        var request = new DocumentationPreviewRequest(project.CurrentSha256, [new("//Controller/Tags/Tag[@Name='Record']", "description", "en-US", "Another client's description")]);
        var external = await client.PostAsync<DocumentationPreview>($"/api/projects/{project.Id}/documentation/preview", request);
        await client.PostAsync<ProjectInfo>($"/api/projects/{project.Id}/documentation/apply", new DocumentationApplyRequest(request, external.ReviewSha256));
        var rejected = false; try { await LoadAsync(); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected || pending.Count != 1) throw new InvalidOperationException("A changed source silently adopted the pending editor's revision.");
        await DiscardAsync(); Select("Record", "description", "es-ES"); var saved = await ExportFileAsync(Path.Combine(root, "artifacts", "exports", "desktop-documentation-" + project.Id + ".json"));
        if (ProjectService.Hash(await File.ReadAllBytesAsync(saved.Path)) != saved.Sha256) throw new InvalidOperationException("Documentation export hash mismatch.");
        status.Text = "Verified staged multilingual edits, review/apply, stale draft preservation, explicit discard and export. No native/controller access.";
        return ["Stage multiple languages across fields", "Preview before managed apply", "Read back Unicode and preserve other translations", "Retain stale drafts across concurrent edits", "Explicit discard/reload", "Save verified full documentation artifact"];
    }
}
