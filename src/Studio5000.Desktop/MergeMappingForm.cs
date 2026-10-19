using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class MergeMappingForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly ComboBox saved = new() { Width = 270, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox name = new() { Width = 250, Text = "Tag value mapping" };
    private readonly DataGridView pairs = new() { Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = true };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 62, Padding = new(8), Text = "Whole-tag names only. Choose actual projects and program scopes in the merge window. No values are stored here." };
    private bool fixture, busy;
    public SavedMergeMapping? Selection { get; private set; }
    public MergeMappingForm(ServiceClient client, MergeTagPair[] initial)
    {
        this.client = client; Text = "Reusable tag-merge mappings"; Size = new(1000, 620); MinimumSize = new(820, 500); Font = new("Segoe UI", 10);
        pairs.Columns.Add("SourceTag", "Source whole-tag name"); pairs.Columns.Add("DestinationTag", "Destination whole-tag name");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 94, Padding = new(8), AutoScroll = true };
        top.Controls.Add(saved); Button(top, "Refresh saved", () => RefreshAsync()); Button(top, "Load selected", LoadAsync); top.SetFlowBreak(top.Controls[^1], true);
        top.Controls.Add(name); Button(top, "Save new", () => SaveAsync(false)); Button(top, "Update selected", () => SaveAsync(true)); Button(top, "Delete selected", DeleteAsync);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(8) };
        Button(bottom, "Use saved mapping", UseAsync); Button(bottom, "Import JSON as new…", ImportAsync); Button(bottom, "Export loaded JSON…", ExportAsync);
        bottom.Controls.Add(new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel });
        Controls.Add(pairs); Controls.Add(status); Controls.Add(bottom); Controls.Add(top); SetPairs(initial);
        Shown += async (_, _) => { if (!fixture) await Run(() => RefreshAsync()); };
    }
    private void Button(Control parent, string label, Func<Task> action)
    { var b = new Button { Text = label, AutoSize = true }; parent.Controls.Add(b); b.Click += async (_, _) => await Run(action); }
    private async Task Run(Func<Task> action)
    { if (busy || fixture) return; busy = true; Enabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; Enabled = true; } }
    private void SetPairs(MergeTagPair[] tags) { pairs.Rows.Clear(); foreach (var pair in tags) pairs.Rows.Add(pair.SourceTag, pair.DestinationTag); }
    private MergeMappingTemplate Template()
    {
        pairs.EndEdit(); var template = new MergeMappingTemplate(1, name.Text.Trim(), pairs.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
            .Select(r => new MergeTagPair(Convert.ToString(r.Cells[0].Value) ?? "", Convert.ToString(r.Cells[1].Value) ?? "")).ToArray());
        MergeMappingTemplates.Validate(template); return template;
    }
    private async Task RefreshAsync(string? id = null)
    {
        id ??= (saved.SelectedItem as SavedReviewChoice)?.Summary.Id;
        var choices = (await client.GetAsync<ReviewSummary[]>("/api/merge-mappings")).Select(s => new SavedReviewChoice(s)).ToArray();
        saved.DataSource = choices; saved.SelectedItem = choices.FirstOrDefault(c => c.Summary.Id == id) ?? choices.FirstOrDefault();
    }
    private async Task LoadAsync()
    {
        var id = (saved.SelectedItem as SavedReviewChoice)?.Summary.Id ?? throw new InvalidOperationException("Select a saved mapping.");
        Selection = await client.GetAsync<SavedMergeMapping>("/api/merge-mappings/" + id); name.Text = Selection.Template.Name; SetPairs(Selection.Template.Tags);
        await RefreshAsync(id); status.Text = $"Loaded {Selection.Template.Tags.Length} name pairs. Values and scopes will be checked when this mapping is used.";
    }
    private async Task SaveAsync(bool update)
    {
        var chosen = update ? (saved.SelectedItem as SavedReviewChoice)?.Summary ?? throw new InvalidOperationException("Select a saved mapping to update.") : null;
        if (chosen is not null && Selection?.Id != chosen.Id) throw new InvalidOperationException("Load the selected mapping before updating it.");
        Selection = await client.PostAsync<SavedMergeMapping>("/api/merge-mappings", new MergeMappingSaveRequest(JsonSerializer.SerializeToElement(Template(), ProjectService.Json), chosen?.Id, chosen is null ? null : Selection!.Revision));
        await RefreshAsync(Selection.Id); status.Text = $"Saved {Selection.Template.Name} revision {Selection.Revision}. No project or value changes.";
    }
    private async Task DeleteAsync()
    {
        var chosen = (saved.SelectedItem as SavedReviewChoice)?.Summary ?? throw new InvalidOperationException("Select a saved mapping.");
        await client.PostAsync<object>($"/api/merge-mappings/{chosen.Id}/delete", new ReviewDeleteRequest(chosen.Revision));
        Selection = null; await RefreshAsync(); status.Text = "Mapping deleted. Project snapshots are unchanged.";
    }
    private Task UseAsync()
    {
        RequireSaved(); DialogResult = DialogResult.OK; Close(); return Task.CompletedTask;
    }
    private SavedMergeMapping RequireSaved()
    {
        var current = Template();
        if (Selection is null || Selection.Id != (saved.SelectedItem as SavedReviewChoice)?.Summary.Id ||
            JsonSerializer.Serialize(current, ProjectService.Json) != JsonSerializer.Serialize(Selection.Template, ProjectService.Json))
            throw new InvalidOperationException("Load the selected mapping, or save your edited name pairs before using them.");
        return Selection;
    }
    private async Task ImportAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Portable tag mappings|*.json", Title = "Import JSON as a new saved mapping" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await ImportFileAsync(dialog.FileName);
    }
    private async Task ImportFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MergeMappingTemplates.MaximumBytes) throw new InvalidOperationException("Mapping JSON exceeds 128 KiB.");
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 8 });
        Selection = await client.PostAsync<SavedMergeMapping>("/api/merge-mappings/import", new MergeMappingImportRequest(document.RootElement));
        name.Text = Selection.Template.Name; SetPairs(Selection.Template.Tags); await RefreshAsync(Selection.Id);
        status.Text = $"Imported {Selection.Template.Tags.Length} pairs as a new mapping. Choose projects and scopes when using it.";
    }
    private async Task ExportAsync()
    {
        RequireSaved();
        using var dialog = new SaveFileDialog { Filter = "Portable tag mappings|*.json", FileName = "tag-merge-mapping.json", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var exported = await ExportFileAsync(dialog.FileName);
        status.Text = "Saved portable name pairs to " + exported.Path + ". Review names before sharing.";
    }
    private async Task<SavedArtifact> ExportFileAsync(string path)
    {
        var mapping = RequireSaved();
        var artifact = await client.PostAsync<Artifact>($"/api/merge-mappings/{mapping.Id}/export", new MergeMappingExportRequest(mapping.Revision));
        return await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(path));
    }
    public void LoadForTest() { fixture = true; name.Text = "Reusable process values"; SetPairs([new("Enabled", "OtherEnabled"), new("Record", "Record")]); }
    public string[] ExerciseOfflineWorkflowForTest()
    {
        if (!fixture) throw new InvalidOperationException("Generated off-screen validation only.");
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        async Task ExerciseAsync()
        {
            await SaveAsync(false); var id = Selection!.Id;
            Check(Selection is { Revision: 0, Template.Tags.Length: 2 }, "Mapping editor saves edited tag pairs through the shared service");
            pairs.Rows[0].Cells[1].Value = "MappedEnabled"; await SaveAsync(true);
            Check(Selection!.Id == id && Selection.Revision == 1, "Mapping editor updates the selected saved definition with its loaded revision");
            pairs.Rows[0].Cells[1].Value = "UnsavedName"; await LoadAsync();
            Check(Convert.ToString(pairs.Rows[0].Cells[1].Value) == "MappedEnabled", "Loading a mapping restores the saved pairs rather than unsaved edits");
            await client.PostAsync<SavedMergeMapping>("/api/merge-mappings", new MergeMappingSaveRequest(JsonSerializer.SerializeToElement(
                Selection.Template with { Name = "Updated by another client" }, ProjectService.Json), id, Selection.Revision));
            await RefreshAsync(id); var rejected = false;
            try { await SaveAsync(true); } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { rejected = true; }
            Check(rejected, "Refreshing summaries does not let an older loaded editor overwrite another client's definition");
            await LoadAsync(); await DeleteAsync(); Check(Selection is null, "Deleting a mapping clears the editor's loaded definition");
            await SaveAsync(false);
            var original = Selection!; var path = Path.Combine(ServiceClient.FindRoot(), "artifacts", "exports", "gui-mapping-roundtrip.json");
            var exported = await ExportFileAsync(path); await ImportFileAsync(path);
            Check(Selection!.Id != original.Id && Selection.Revision == 0 && Selection.Template.Tags.SequenceEqual(original.Template.Tags) && exported.Sha256 == ProjectService.Hash(File.ReadAllBytes(path)),
                "GUI exports through the guarded artifact service and imports identical pairs as a distinct saved definition");
            var retained = Selection; await File.WriteAllTextAsync(path + ".invalid.json", "{\"schemaVersion\":1,\"name\":\"Unsafe fields\",\"tags\":[],\"values\":42}");
            rejected = false; try { await ImportFileAsync(path + ".invalid.json"); } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest) { rejected = true; }
            Check(rejected && Selection == retained, "GUI rejects nonportable mapping fields while preserving the loaded definition");
        }
        WorkflowValidation.Run(ExerciseAsync); return checks.ToArray();
    }
}
