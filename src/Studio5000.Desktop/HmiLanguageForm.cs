using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class HmiLanguageForm : WorkspaceForm
{
    private sealed record Row(HmiLanguageRecord Record, HmiLanguageText Text);
    private readonly ServiceClient client;
    private readonly HmiLanguageInfo? initial;
    private readonly ComboBox platform = new() { Width = 145, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox singleLanguage = new() { Width = 130, PlaceholderText = "FT text language" };
    private readonly ComboBox workspaces = new() { Width = 270, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly TextBox query = new() { Width = 220, PlaceholderText = "Search context / translations" };
    private readonly DataGridView records = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox language = new() { Width = 140, PlaceholderText = "Language, e.g. zh-CN" };
    private readonly TextBox newLanguage = new() { Width = 140, PlaceholderText = "New language code" };
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, MaxLength = 32767, ScrollBars = ScrollBars.Both, Font = new("Segoe UI", 11) };
    private readonly TextBox review = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly Label selection = new() { Dock = DockStyle.Top, Height = 62, Padding = new(7) };
    private readonly Label metadata = new() { Dock = DockStyle.Top, Height = 58, Padding = new(7) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 48, Padding = new(7) };
    private readonly Button apply = new() { Text = "Apply reviewed translations", AutoSize = true, Enabled = false };
    private readonly Dictionary<string, HmiLanguageEdit> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);
    private HmiLanguageInfo? workspace;
    private HmiLanguagePage? page;
    private HmiLanguagePreview? preview;
    private HmiLanguagePreviewRequest? proposed;
    private int offset;
    private bool busy, loading, fixture;
    private bool HasDraft => pending.Count != 0 || added.Count != 0;
    public HmiLanguageForm(ServiceClient client, string selectedPlatform = "ftview-me", HmiLanguageInfo? initial = null)
    {
        this.client = client; this.initial = initial;
        Text = "HMI multilingual text — FactoryTalk View ME/SE and View Designer"; Size = new(1450, 950); MinimumSize = new(1120, 800); Font = new("Segoe UI", 10);
        var heading = new Label { Text = "HMI LANGUAGES  /  NATIVE STRING IDENTITIES", Dock = DockStyle.Top, Height = 48, Padding = new(12), ForeColor = Color.White, BackColor = Color.FromArgb(24, 40, 61) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 88, Padding = new(7) };
        platform.Items.AddRange(["ftview-me", "ftview-se", "view-designer"]); platform.SelectedItem = selectedPlatform; toolbar.Controls.Add(platform); toolbar.Controls.Add(singleLanguage);
        Button(toolbar, "Open language export…", OpenAsync); toolbar.Controls.Add(workspaces); toolbar.Controls.Add(query);
        Button(toolbar, "Search / refresh", async () => { offset = 0; await LoadAsync(); });
        Button(toolbar, "Previous", async () => { offset = Math.Max(0, offset - 100); await LoadAsync(); });
        Button(toolbar, "Next", async () => { if (page is not null && offset + 100 < page.Total) { offset += 100; await LoadAsync(); } });
        Button(toolbar, "Export native language file…", ExportAsync); Button(toolbar, "Compare Studio re-export…", VerifyAsync);
        foreach (var (name, width) in new[] { ("Sheet", 85), ("Native identity / context", 360), ("Language", 90), ("Translation", 470), ("Status", 120) }) records.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = width });
        var editorBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new(7) };
        editorBar.Controls.Add(language); Button(editorBar, "Stage text", () => { Stage(); return Task.CompletedTask; }); editorBar.Controls.Add(newLanguage); Button(editorBar, "Stage language column", () => { AddLanguage(); return Task.CompletedTask; });
        var editor = new Panel { Dock = DockStyle.Fill }; editor.Controls.Add(text); editor.Controls.Add(editorBar); editor.Controls.Add(selection);
        var bottom = new SplitContainer { Dock = DockStyle.Fill }; bottom.Panel1.Controls.Add(editor); bottom.Panel2.Controls.Add(review);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal }; split.Panel1.Controls.Add(records); split.Panel2.Controls.Add(bottom);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new(7) };
        Button(actions, "Preview translations", PreviewAsync); actions.Controls.Add(apply); Button(actions, "Discard draft / reload", DiscardAsync);
        var boundary = new Label { Text = "Offline exports. All existing languages and keys are retained. Native Studio import, layout/fonts and runtime verification remain separate.", Dock = DockStyle.Bottom, Height = 38, Padding = new(7), BackColor = Color.FromArgb(255, 245, 220) };
        Controls.Add(split); Controls.Add(actions); Controls.Add(status); Controls.Add(boundary); Controls.Add(metadata); Controls.Add(toolbar); Controls.Add(heading);
        workspaces.SelectedIndexChanged += async (_, _) => { if (!loading) await Run(SelectAsync); };
        records.SelectionChanged += (_, _) => ShowEditor(); apply.Click += async (_, _) => await Run(ApplyAsync);
        Shown += async (_, _) => { split.SplitterDistance = Math.Max(220, split.Height / 2); bottom.SplitterDistance = bottom.Width / 2; if (!fixture) await Run(InitializeAsync); };
        FormClosing += (_, e) => { if (!fixture && HasDraft) { e.Cancel = true; status.Text = "Apply the reviewed translations or Discard draft / reload before closing."; } };
    }
    private void Button(Control parent, string label, Func<Task> action)
    { var button = new Button { Text = label, AutoSize = true }; button.Click += async (_, _) => await Run(action); parent.Controls.Add(button); }
    private async Task Run(Func<Task> action)
    { if (busy) return; busy = true; Enabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; Enabled = true; } }
    private async Task InitializeAsync() => await RefreshListAsync(initial?.Id);
    private async Task RefreshListAsync(string? id)
    {
        var values = await client.GetAsync<HmiLanguageInfo[]>("/api/hmi-languages"); loading = true; workspaces.DataSource = values; workspaces.SelectedItem = values.FirstOrDefault(p => p.Id == id) ?? values.FirstOrDefault(); loading = false;
        if (workspaces.SelectedItem is not null) await SelectAsync();
    }
    private async Task OpenAsync()
    {
        if (HasDraft) throw new InvalidOperationException("Apply or discard the current draft before opening another export.");
        using var dialog = new OpenFileDialog { Filter = "HMI language exports|*.xls;*.xlsx;*.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var value = await client.PostAsync<HmiLanguageInfo>("/api/hmi-languages", new HmiLanguageRegisterRequest(dialog.FileName, (string)platform.SelectedItem!, string.IsNullOrWhiteSpace(singleLanguage.Text) ? null : singleLanguage.Text.Trim()));
        await RefreshListAsync(value.Id);
    }
    private async Task SelectAsync()
    {
        if (HasDraft) { loading = true; workspaces.SelectedItem = ((HmiLanguageInfo[])workspaces.DataSource!).FirstOrDefault(p => p.Id == workspace?.Id); loading = false; throw new InvalidOperationException("Apply or discard staged translations before switching workspaces."); }
        workspace = workspaces.SelectedItem as HmiLanguageInfo; offset = 0; InvalidateReview(); if (workspace is not null) { platform.SelectedItem = workspace.Platform; singleLanguage.Text = workspace.Language ?? ""; await LoadAsync(); }
    }
    private void InvalidateReview() { preview = null; proposed = null; apply.Enabled = false; }
    private async Task LoadAsync()
    {
        if (workspace is null) throw new InvalidOperationException("Open or select a native HMI language export.");
        var latest = await client.GetAsync<HmiLanguageInfo>("/api/hmi-languages/" + workspace.Id);
        if (latest.CurrentSha256 != workspace.CurrentSha256 && HasDraft) throw new InvalidOperationException("Source changed. The draft is preserved; discard/reload before editing the new revision.");
        workspace = latest;
        page = await client.PostAsync<HmiLanguagePage>($"/api/hmi-languages/{workspace.Id}/browse", new HmiLanguageQuery(workspace.CurrentSha256, string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim(), Offset: offset));
        records.Rows.Clear();
        foreach (var record in page.Records)
        foreach (var value in record.Texts)
        {
            var display = value.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ↵ ");
            records.Rows[records.Rows.Add(record.Sheet, string.Join(" / ", record.Context), value.Language, display, value.Writable ? "editable" : "read-only")].Tag = new Row(record, value);
        }
        metadata.Text = $"{workspace.Platform} • r{workspace.Revision} • {workspace.CurrentSha256[..12]} • {string.Join(", ", page.Languages)}\nStrings {Math.Min(offset + 1, page.Total)}–{Math.Min(offset + 100, page.Total)} / {page.Total} • {pending.Count} text edits, {added.Count} new language columns staged.";
        ShowEditor();
    }
    private Row? Selected => records.CurrentRow?.Tag as Row;
    private void ShowEditor()
    {
        if (Selected is not { } row) { selection.Text = "Select a native string and explicit language."; return; }
        language.Text = row.Text.Language; text.Text = pending.GetValueOrDefault(row.Record.Key + ":" + row.Text.Language)?.Text ?? row.Text.Text;
        selection.Text = string.Join(" / ", row.Record.Context) + "\nKey: " + row.Record.Key[..20] + " • Keys/context cannot be edited.";
    }
    private void Stage()
    {
        if (Selected is not { } row || !row.Text.Writable) throw new InvalidOperationException("Select an editable native string.");
        var locale = language.Text.Trim(); var key = row.Record.Key + ":" + locale;
        if (!pending.ContainsKey(key) && pending.Count >= 200) throw new InvalidOperationException("Apply or discard the 200 staged edits first.");
        pending[key] = new(row.Record.Key, locale, text.Text); InvalidateReview(); ShowDraft();
    }
    private void AddLanguage()
    { if (workspace is null || string.IsNullOrWhiteSpace(newLanguage.Text)) throw new InvalidOperationException("Select a workspace and enter a new language code."); added.Add(newLanguage.Text.Trim()); InvalidateReview(); ShowDraft(); }
    private void ShowDraft() { review.Text = JsonSerializer.Serialize(new { edits = pending.Values, addLanguages = added }, ProjectService.Json); status.Text = "Staged locally. Preview checks the complete batch before any managed change."; }
    private async Task PreviewAsync()
    {
        if (workspace is null || !HasDraft) throw new InvalidOperationException("Stage text or a new language column first.");
        proposed = new(workspace.CurrentSha256, pending.Values.ToArray(), added.ToArray()); preview = null; apply.Enabled = false;
        preview = await client.PostAsync<HmiLanguagePreview>($"/api/hmi-languages/{workspace.Id}/preview", proposed); review.Text = JsonSerializer.Serialize(preview, ProjectService.Json); apply.Enabled = true; status.Text = "Review all before/after translations and new columns, then apply the managed revision.";
    }
    private async Task ApplyAsync()
    {
        if (workspace is null || preview is null || proposed is null) throw new InvalidOperationException("Preview the draft first.");
        workspace = await client.PostAsync<HmiLanguageInfo>($"/api/hmi-languages/{workspace.Id}/apply", new HmiLanguageApplyRequest(proposed, preview.ReviewSha256));
        pending.Clear(); added.Clear(); InvalidateReview(); await LoadAsync(); status.Text = $"Saved managed r{workspace.Revision}. Export a new language file for native Studio import.";
    }
    private async Task DiscardAsync() { pending.Clear(); added.Clear(); InvalidateReview(); review.Clear(); await LoadAsync(); }
    private async Task<SavedArtifact> ExportFileAsync(string path)
    {
        if (workspace is null) throw new InvalidOperationException("Select a language workspace.");
        if (HasDraft) throw new InvalidOperationException("Apply or discard the draft before exporting.");
        var artifact = await client.PostAsync<Artifact>($"/api/hmi-languages/{workspace.Id}/export", new HmiLanguageExportRequest(workspace.CurrentSha256));
        return await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(path));
    }
    private async Task ExportAsync()
    {
        if (workspace is null) throw new InvalidOperationException("Select a language workspace.");
        using var dialog = new SaveFileDialog { Filter = $"Native language export|*.{workspace.Format}", FileName = workspace.Name + "-translated." + workspace.Format };
        if (dialog.ShowDialog(this) == DialogResult.OK) status.Text = "Saved new language file: " + (await ExportFileAsync(dialog.FileName)).Path;
    }
    private async Task VerifyAsync()
    {
        if (workspace is null) throw new InvalidOperationException("Select a language workspace.");
        using var dialog = new OpenFileDialog { Filter = "Studio language re-export|*.xls;*.xlsx;*.txt" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var verification = await client.PostAsync<HmiLanguageVerification>($"/api/hmi-languages/{workspace.Id}/verify", new HmiLanguageVerifyRequest(workspace.CurrentSha256, dialog.FileName));
        review.Text = JsonSerializer.Serialize(verification, ProjectService.Json); status.Text = verification.Matches ? "All identities and language strings match. Native provenance/layout/runtime acceptance remain separate." : $"{verification.DifferenceCount} differences; first {verification.Differences.Length} shown.";
    }
    internal async Task<string[]> ExerciseOfflineWorkflowForTestAsync(string root)
    {
        fixture = true; await InitializeAsync();
        var key = page!.Records[0].Key;
        void Select(string locale)
        {
            var row = records.Rows.Cast<DataGridViewRow>().Single(r => r.Tag is Row x && x.Record.Key == key && x.Text.Language == locale);
            records.CurrentCell = row.Cells[0]; ShowEditor();
        }
        Select("en-US");
        language.Text = "zh-CN"; text.Text = "翻译验证\r\nReady"; Stage(); newLanguage.Text = "fr-FR"; AddLanguage(); language.Text = "fr-FR"; text.Text = "Prêt"; Stage();
        await PreviewAsync(); if (preview!.Changes.Length != 2 || !apply.Enabled) throw new InvalidOperationException("Language review lost staged edits."); await ApplyAsync();
        var record = page!.Records.Single(r => r.Key == key); if (record.Texts.Single(t => t.Language == "zh-CN").Text != "翻译验证\r\nReady" || record.Texts.Single(t => t.Language == "fr-FR").Text != "Prêt") throw new InvalidOperationException("Unicode/new-language readback failed.");
        Select("zh-CN"); text.Text = "Pending"; Stage();
        var external = new HmiLanguagePreviewRequest(workspace!.CurrentSha256, [new(key, "en-US", "Updated externally")]);
        var reviewed = await client.PostAsync<HmiLanguagePreview>($"/api/hmi-languages/{workspace.Id}/preview", external);
        await client.PostAsync<HmiLanguageInfo>($"/api/hmi-languages/{workspace.Id}/apply", new HmiLanguageApplyRequest(external, reviewed.ReviewSha256));
        var rejected = false; try { await LoadAsync(); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected || pending.Count != 1) throw new InvalidOperationException("Concurrent revision discarded the pending language draft.");
        await DiscardAsync(); var saved = await ExportFileAsync(Path.Combine(root, "artifacts", "exports", "desktop-languages-" + workspace.Id + ".xls"));
        var suppliedReexport = Path.Combine(root, "examples", "desktop-language-readback-" + workspace.Id + ".xls"); File.Copy(saved.Path, suppliedReexport);
        var verify = await client.PostAsync<HmiLanguageVerification>($"/api/hmi-languages/{workspace.Id}/verify", new HmiLanguageVerifyRequest(workspace.CurrentSha256, suppliedReexport));
        if (!verify.Matches || ProjectService.Hash(File.ReadAllBytes(saved.Path)) != saved.Sha256) throw new InvalidOperationException("GUI language artifact readback failed.");
        status.Text = "Verified staged Unicode translations/new columns, reviewed apply, concurrent draft retention, explicit discard, export and comparison.";
        return ["Stage multilingual text/new columns", "Preview reviewed managed changes", "Read back Unicode while preserving native identities", "Retain stale drafts across concurrent revisions", "Explicit discard/reload", "Export native language file and compare all strings"];
    }
}
