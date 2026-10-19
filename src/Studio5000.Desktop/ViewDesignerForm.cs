using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class ViewDesignerForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly ComboBox projects = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly ComboBox files = new() { Dock = DockStyle.Top, Height = 32, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Path" };
    private readonly DataGridView properties = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true,
        AllowUserToAddRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox value = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 11) };
    private readonly TextBox review = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10), WordWrap = false };
    private readonly TextBox source = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10), WordWrap = false };
    private readonly TextBox target = new() { Width = 230 };
    private readonly TextBox panel = new() { Width = 200, PlaceholderText = "Exact target panel type" };
    private readonly TextBox language = new() { Width = 90, Text = "en-US" };
    private readonly TextBox planId = new() { Width = 260, PlaceholderText = "Import plan ID" };
    private readonly TextBox reexport = new() { Width = 400, PlaceholderText = "Export folder with ViewApplication.hmi" };
    private readonly Label selection = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(5), Text = "Select an existing property." };
    private readonly ToolStripStatusLabel status = new() { Text = "Export a project in View Designer, then open its export folder here." };
    private readonly Button apply = new() { Text = "Apply preview", AutoSize = true, Enabled = false };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private ProjectInfo? project;
    private ViewDesignerPreview? draft;
    private ViewDesignerImportPlan? importPlan;
    private string? activeFile;
    private bool busy, loading, selfTest;

    public ViewDesignerForm(ServiceClient client)
    {
        this.client = client; Text = "Studio 5000 View Designer — exported project workspace";
        Size = new(1380, 920); MinimumSize = new(1100, 750); Font = new("Segoe UI", 10);
        var heading = new Label { Text = "VIEW DESIGNER  /  PANELVIEW 5000", Dock = DockStyle.Top, Height = 50, Padding = new(14),
            BackColor = Color.FromArgb(24, 40, 61), ForeColor = Color.White, Font = new("Segoe UI", 13, FontStyle.Bold) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(6) };
        Button(bar, "Open export folder…", OpenAsync); bar.Controls.Add(projects); Button(bar, "Refresh", RefreshAsync);
        Button(bar, "Installation / support", async () => ShowJson(await client.GetAsync<JsonElement>("/api/viewdesigner/status")));
        var context = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(6) };
        Labelled(context, "Native target project", target); Labelled(context, "Panel", panel); Labelled(context, "Language", language);
        Button(context, "Prepare import ZIP", PrepareAsync); Button(context, "Save ZIP…", SavePackageAsync);
        var verification = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(6) };
        verification.Controls.Add(planId); verification.Controls.Add(reexport); Button(verification, "Compare re-export", VerifyAsync);
        var boundary = new Label { Text = "Managed export edits only. Import, build, save and runtime validation in View Designer remain pending.",
            Dock = DockStyle.Bottom, Height = 32, Padding = new(8), ForeColor = Color.FromArgb(120, 65, 0), BackColor = Color.FromArgb(255, 245, 220) };
        var layout = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 650 };
        layout.Panel1.Padding = new(8); layout.Panel1.Controls.Add(properties); layout.Panel1.Controls.Add(files);
        var edit = new TabPage("Property value (HMI syntax)"); edit.Controls.Add(value); edit.Controls.Add(selection);
        var previewPage = new TabPage("Review / handoff"); previewPage.Controls.Add(review);
        var sourcePage = new TabPage("Source .hmi"); sourcePage.Controls.Add(source);
        tabs.TabPages.AddRange([edit, previewPage, sourcePage]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(6) };
        Button(actions, "Preview property edit", PreviewAsync); actions.Controls.Add(apply);
        layout.Panel2.Padding = new(8); layout.Panel2.Controls.Add(tabs); layout.Panel2.Controls.Add(actions);
        var statusBar = new StatusStrip(); statusBar.Items.Add(status);
        Controls.Add(layout); Controls.Add(verification); Controls.Add(context); Controls.Add(bar); Controls.Add(heading); Controls.Add(boundary); Controls.Add(statusBar);
        projects.SelectedIndexChanged += async (_, _) => { InvalidateDraft(); if (!loading) await Run(LoadProjectAsync); };
        files.SelectedIndexChanged += async (_, _) => { InvalidateDraft(); if (!loading) await Run(LoadFileAsync); };
        properties.SelectionChanged += (_, _) => SelectProperty(); value.TextChanged += (_, _) => InvalidateDraft();
        apply.Click += async (_, _) => await Run(ApplyAsync);
        Shown += async (_, _) => { layout.SplitterDistance = Width / 2; if (!selfTest) await Run(RefreshAsync); };
    }

    private void Button(Control parent, string text, Func<Task> action)
    { var button = new Button { Text = text, AutoSize = true }; button.Click += async (_, _) => await Run(action); parent.Controls.Add(button); }
    private static void Labelled(Control parent, string text, Control input)
    { parent.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new(3, 7, 0, 0) }); parent.Controls.Add(input); }
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UseWaitCursor = true;
        try { await action(); }
        catch (Exception ex) { status.Text = ex.Message; MessageBox.Show(this, ex.Message, "View Designer workspace", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; UseWaitCursor = false; }
    }
    private void InvalidateDraft() { draft = null; apply.Enabled = false; }
    private void ShowJson<T>(T data) { review.Text = JsonSerializer.Serialize(data, ProjectService.Json); tabs.SelectedIndex = 1; }
    private async Task OpenAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select the View Designer export folder containing ViewApplication.hmi" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        project = await client.PostAsync<ProjectInfo>("/api/viewdesigner/projects", new RegisterRequest(dialog.SelectedPath));
        await RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        var all = await client.GetAsync<ProjectInfo[]>("/api/viewdesigner/projects"); var id = project?.Id;
        loading = true; projects.DataSource = all; if (id != null) projects.SelectedItem = all.FirstOrDefault(p => p.Id == id); loading = false;
        await LoadProjectAsync();
    }
    private async Task LoadProjectAsync()
    {
        InvalidateDraft(); importPlan = null; planId.Clear(); reexport.Clear();
        project = projects.SelectedItem as ProjectInfo;
        if (project is null) { properties.DataSource = null; return; }
        var inspection = await client.GetAsync<ViewDesignerInspection>($"/api/viewdesigner/projects/{project.Id}"); project = inspection.Project;
        loading = true; files.DataSource = inspection.Files; loading = false;
        target.Text = project.Name;
        status.Text = $"{project.Name} • revision {project.Revision} • {inspection.Files.Length} files • source preserved";
        await LoadFileAsync();
    }
    private async Task LoadFileAsync()
    {
        InvalidateDraft(); activeFile = null; source.Clear(); value.Clear(); properties.DataSource = null;
        if (project is null || files.SelectedItem is not ViewDesignerFile file) return;
        if (!file.Editable) { selection.Text = "Asset preserved unchanged: " + file.Path; return; }
        var content = await client.GetAsync<ViewDesignerContent>($"/api/viewdesigner/projects/{project.Id}/file?file={Uri.EscapeDataString(file.Path)}");
        activeFile = file.Path; source.Text = content.Document.Text; properties.DataSource = content.Document.Properties; SelectProperty();
    }
    private void SelectProperty()
    {
        InvalidateDraft();
        if (properties.CurrentRow?.DataBoundItem is not HmiProperty property) return;
        selection.Text = property.Selector; value.Text = property.Value;
    }
    private async Task PreviewAsync()
    {
        if (project is null || activeFile is null || properties.CurrentRow?.DataBoundItem is not HmiProperty property) return;
        draft = await client.PostAsync<ViewDesignerPreview>($"/api/viewdesigner/projects/{project.Id}/previews",
            new ViewDesignerPreviewRequest(project.CurrentSha256, [new(activeFile, property.Selector, value.Text)]));
        review.Text = draft.Diff + "\r\nTree SHA-256: " + draft.ResultSha256; tabs.SelectedIndex = 1; apply.Enabled = true;
    }
    private async Task ApplyAsync()
    {
        if (project is null || draft is null) return;
        var file = activeFile;
        project = await client.PostAsync<ProjectInfo>($"/api/viewdesigner/projects/{project.Id}/previews/{draft.Id}/apply", new ApplyRequest(draft.BaseSha256));
        await LoadFileAsync(); status.Text = $"Applied working-copy revision {project.Revision}: {file}. Native import remains pending.";
        importPlan = null; planId.Clear(); reexport.Clear();
    }
    private async Task PrepareAsync()
    {
        if (project is null) return;
        importPlan = await client.PostAsync<ViewDesignerImportPlan>($"/api/viewdesigner/projects/{project.Id}/imports",
            new ViewDesignerImportRequest(project.CurrentSha256, target.Text, panel.Text, language.Text));
        planId.Text = importPlan.Id; reexport.Text = importPlan.ReexportDirectory; ShowJson(importPlan);
    }
    private async Task SavePackageAsync()
    {
        if (importPlan is null) { status.Text = "Prepare an import package first."; return; }
        using var dialog = new SaveFileDialog { Filter = "ZIP package|*.zip", FileName = "ViewDesigner-" + importPlan.Id + ".zip",
            InitialDirectory = Path.Combine(ServiceClient.FindRoot(), "artifacts", "exports") };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            ShowJson(await client.PostAsync<SavedArtifact>($"/api/artifacts/{importPlan.Package.Id}/save", new SaveArtifactRequest(dialog.FileName)));
    }
    private async Task VerifyAsync()
    {
        if (string.IsNullOrWhiteSpace(planId.Text)) return;
        ShowJson(await client.PostAsync<ViewDesignerVerification>($"/api/viewdesigner/imports/{planId.Text.Trim()}/verify", new VerifyGraphicsImportRequest(reexport.Text.Trim())));
    }

    public void LoadForTest()
    {
        selfTest = true; loading = true;
        project = new(new string('0', 32), "PanelView development export", "Example export", new string('a', 64), new string('b', 64), 1, DateTimeOffset.UtcNow, "view-designer");
        projects.Items.Add(project); projects.SelectedIndex = 0;
        files.Items.Add(new ViewDesignerFile("User-Defined Screens/Overview.hmi", new string('c', 64), 1024, true)); files.SelectedIndex = 0;
        properties.DataSource = new HmiProperty[] { new("/Screen:Overview/TextDisplay:Status/@Text", "\"Ready\""),
            new("/Screen:Overview/NumericDisplay:ProcessValue/@Value", "0 -> \"::LGX.Process.Count\""), new("/Screen:Overview/@Width", "800") };
        panel.Text = "PanelView 5510 — same panel as export"; target.Text = "Development HMI";
        review.Text = "Property edit preview\r\n- \"Ready\"\r\n+ \"Process ready\"\r\n\r\nNative import, build and runtime validation pending.";
        loading = false; SelectProperty();
    }
}
