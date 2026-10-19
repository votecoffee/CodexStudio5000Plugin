using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class MainForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly ListBox projects = new() { Dock = DockStyle.Fill, DisplayMember = "Name" };
    private readonly DataGridView elements = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly TextBox editor = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly TextBox review = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly TextBox xpath = new() { Width = 620 };
    private readonly ComboBox operation = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox attribute = new() { Width = 100, PlaceholderText = "Attribute" };
    private readonly TextBox search = new() { Width = 160, PlaceholderText = "Filter component name" };
    private readonly TextBox graphicsPlanId = new() { Width = 225, PlaceholderText = "FactoryTalk import plan ID" };
    private readonly ToolStripStatusLabel status = new() { Text = "Connect to the shared local service." };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly Button apply = new() { Text = "Apply preview", AutoSize = true, Enabled = false };
    private ProjectInfo? project;
    private ChangePreview? draft;
    private bool busy;
    private bool suppress;

    public MainForm(ServiceClient client)
    {
        this.client = client;
        Text = WorkspaceIdentity.Name + " — Logix + FactoryTalk + View Designer";
        Size = new Size(1400, 920); MinimumSize = new Size(1000, 700);
        Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(244, 246, 249);
        var header = new Label { Text = WorkspaceIdentity.Name + "  /  LOGIX + FACTORYTALK + VIEW DESIGNER", Dock = DockStyle.Top, Height = 54,
            Padding = new Padding(16), BackColor = Color.FromArgb(24, 40, 61), ForeColor = Color.White, Font = new Font("Segoe UI", 13, FontStyle.Bold) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(8) };
        AddButton(toolbar, "Open project…", OpenAsync);
        AddButton(toolbar, "Refresh", () => RefreshAsync());
        toolbar.Controls.Add(search);
        AddButton(toolbar, "Search", LoadElementsAsync);
        AddButton(toolbar, "Export L5X…", () => ExportAsync("l5x"));
        AddButton(toolbar, "Export ACD…", () => ExportAsync("acd"));
        AddButton(toolbar, "Explode ZIP…", () => ExportAsync("explode"));
        AddButton(toolbar, "Audit", AuditAsync);
        AddButton(toolbar, "SDK operations…", () => { using var sdk = new SdkForm(client, project); sdk.ShowDialog(this); return Task.CompletedTask; });
        AddButton(toolbar, "Watch lists…", () => { using var watches = new WatchForm(client, project); watches.ShowDialog(this); return Task.CompletedTask; });
        AddButton(toolbar, "Tag / UDT browser…", () => { using var browser = new TagBrowserForm(client, project); browser.ShowDialog(this); return Task.CompletedTask; });
        AddButton(toolbar, "Compare projects…", () => { using var comparison = new ProjectCompareForm(client, project); comparison.ShowDialog(this); return Task.CompletedTask; });
        AddButton(toolbar, "Merge tag values…", () => { using var merge = new TagValueMergeForm(client, project?.Kind == "logix" ? project : null); merge.ShowDialog(this); return Task.CompletedTask; });
        AddButton(toolbar, "Ladder preview…", () => { using var ladder = new LadderForm(client, project); ladder.ShowDialog(this); return Task.CompletedTask; });
        AddButton(toolbar, "PLC where used…", () => { using var references = new TagReferencesForm(client, project?.Kind == "logix" ? project : null); references.ShowDialog(this); return Task.CompletedTask; });
        AddButton(toolbar, "Capabilities", async () => MessageBox.Show((await client.GetAsync<JsonElement>("/api/capabilities")).ToString(), "Service capabilities"));
        var graphicsToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(8, 3, 8, 3) };
        graphicsToolbar.Controls.Add(new Label { Text = "FACTORYTALK", AutoSize = true, Padding = new Padding(0, 7, 5, 0) });
        AddButton(graphicsToolbar, "Studio status", async () => ShowJson(await client.GetAsync<JsonElement>("/api/factorytalk/status")));
        AddButton(graphicsToolbar, "Open Studio", async () => ShowJson(await client.PostAsync<JsonElement>("/api/factorytalk/launch", new { })));
        AddButton(graphicsToolbar, "Export display XML…", () => ExportAsync("xml"));
        AddButton(graphicsToolbar, "Tag references", async () =>
        {
            if (project is null || !FactoryTalkGraphics.IsFactoryTalk(project.Kind))
                throw new InvalidOperationException("Select an ME or SE display XML working copy first.");
            ShowJson(await client.PostAsync<GraphicsReferenceReport>($"/api/projects/{project.Id}/graphics-references", new GraphicsReferenceRequest(project.CurrentSha256)));
        });
        AddButton(graphicsToolbar, "Resolve references…", () => { using var resolver = new GraphicsReferencesForm(client, project); resolver.ShowDialog(this); return Task.CompletedTask; });
        AddButton(graphicsToolbar, "Prepare import…", PrepareGraphicsAsync);
        graphicsToolbar.Controls.Add(graphicsPlanId);
        AddButton(graphicsToolbar, "Verify re-export", VerifyGraphicsAsync);
        AddButton(graphicsToolbar, "View Designer…", () => { using var view = new ViewDesignerForm(client); view.ShowDialog(this); return Task.CompletedTask; });
        AddButton(graphicsToolbar, "Extensions…", () => { using var extensions = new ExtensionsForm(client); extensions.ShowDialog(this); return Task.CompletedTask; });
        AddButton(graphicsToolbar, "Diagnostics & workflows…", () => { using var diagnostics = new DiagnosticsForm(client, project); diagnostics.ShowDialog(this); return Task.CompletedTask; });
        var layout = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 240, FixedPanel = FixedPanel.Panel1 };
        layout.Panel1.Padding = new Padding(8);
        layout.Panel1.Controls.Add(projects);
        layout.Panel1.Controls.Add(new Label { Text = "MANAGED PROJECTS", Dock = DockStyle.Top, Height = 30 });
        var detail = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 230 };
        detail.Panel1.Controls.Add(elements);
        var editPage = new TabPage("Component editor"); editPage.Controls.Add(editor);
        var reviewPage = new TabPage("Change preview"); reviewPage.Controls.Add(review);
        tabs.TabPages.Add(editPage); tabs.TabPages.Add(reviewPage);
        var targetBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(4) };
        targetBar.Controls.Add(new Label { Text = "XPath", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }); targetBar.Controls.Add(xpath);
        operation.Items.AddRange(["replace_element", "set_text", "set_attribute", "add_element", "remove_element"]); operation.SelectedIndex = 0;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(4) };
        actions.Controls.Add(operation); actions.Controls.Add(attribute);
        AddButton(actions, "Preview edit", PreviewAsync); actions.Controls.Add(apply);
        actions.Controls.Add(new Label { Text = "Edits apply to the managed working copy.", AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
        detail.Panel2.Controls.Add(tabs); detail.Panel2.Controls.Add(targetBar); detail.Panel2.Controls.Add(actions);
        layout.Panel2.Controls.Add(detail);
        var statusBar = new StatusStrip(); statusBar.Items.Add(status);
        Controls.Add(layout); Controls.Add(graphicsToolbar); Controls.Add(toolbar); Controls.Add(header); Controls.Add(statusBar);
        Shown += (_, _) =>
        {
            layout.SplitterDistance = 240;
            detail.SplitterDistance = Math.Min(250, Math.Max(180, detail.Height / 3));
            detail.FixedPanel = FixedPanel.Panel1;
        };
        projects.SelectedIndexChanged += async (_, _) => { if (!suppress) await RunAsync(SelectProjectAsync); };
        elements.SelectionChanged += async (_, _) => { if (!suppress) await RunAsync(LoadSelectedAsync); };
        editor.TextChanged += (_, _) => InvalidateDraft(); xpath.TextChanged += (_, _) => InvalidateDraft();
        operation.SelectedIndexChanged += (_, _) => InvalidateDraft(); attribute.TextChanged += (_, _) => InvalidateDraft();
        apply.Click += async (_, _) => await RunAsync(ApplyAsync);
    }

    private void AddButton(Control parent, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30 };
        button.Click += async (_, _) => await RunAsync(action);
        parent.Controls.Add(button);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; UseWaitCursor = true;
        try { await action(); }
        catch (Exception ex) { status.Text = "Operation failed"; MessageBox.Show(ex.Message, "Studio 5000", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; UseWaitCursor = false; }
    }
    private void InvalidateDraft() { draft = null; apply.Enabled = false; }
    public Task InitializeAsync(string? path) => RunAsync(async () =>
    {
        await RefreshAsync();
        if (path != null) { project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(path)); await RefreshAsync(project.Id); }
    });
    private async Task OpenAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Logix / FactoryTalk files|*.L5X;*.ACD;*.xml|Logix projects|*.L5X;*.ACD|FactoryTalk display XML|*.xml" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        project = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(dialog.FileName));
        await RefreshAsync(project.Id);
    }
    private async Task RefreshAsync(string? selectId = null)
    {
        var all = await client.GetAsync<ProjectInfo[]>("/api/projects");
        selectId ??= project?.Id;
        suppress = true;
        projects.DataSource = all;
        projects.SelectedItem = all.FirstOrDefault(p => p.Id == selectId) ?? all.FirstOrDefault();
        suppress = false;
        await SelectProjectAsync();
    }
    private async Task SelectProjectAsync()
    {
        InvalidateDraft();
        project = projects.SelectedItem as ProjectInfo;
        if (project != null)
        {
            var currentOperation = operation.Text;
            operation.Items.Clear();
            operation.Items.AddRange(FactoryTalkGraphics.IsFactoryTalk(project.Kind)
                ? ["replace_element", "set_text", "set_attribute"] : ["replace_element", "set_text", "set_attribute", "add_element", "remove_element"]);
            operation.SelectedItem = operation.Items.Contains(currentOperation) ? currentOperation : "replace_element";
            await LoadElementsAsync();
        }
    }
    private async Task LoadElementsAsync()
    {
        if (project == null) return;
        var result = await client.GetAsync<Inspection>($"/api/projects/{project.Id}/elements?query={Uri.EscapeDataString(search.Text)}&limit=1000");
        project = result.Project;
        suppress = true; elements.DataSource = result.Elements.ToArray();
        if (elements.Columns["XPath"] is { } column) column.Visible = false;
        if (elements.Columns["Program"] is { } contextColumn) contextColumn.HeaderText = "Program / group";
        var preferred = result.Elements.Select((e, i) => (Element: e, Index: i)).FirstOrDefault(e => e.Element.Kind is "Tag" or "Routine" or "text");
        if (preferred.Element != null && elements.Rows.Count > preferred.Index)
            elements.CurrentCell = elements.Rows[preferred.Index].Cells["Kind"];
        suppress = false;
        status.Text = $"{project.Name}  ·  {project.Kind}  ·  revision {project.Revision}  ·  {result.Elements.Count}/{result.Total} components  ·  SHA {project.CurrentSha256[..12]}";
        await LoadSelectedAsync();
    }
    private async Task LoadSelectedAsync()
    {
        if (project == null || elements.CurrentRow?.DataBoundItem is not ElementInfo element) return;
        var content = await client.GetAsync<ElementContent>($"/api/projects/{project.Id}/element?xpath={Uri.EscapeDataString(element.XPath)}");
        xpath.Text = content.XPath; editor.Text = content.Xml; InvalidateDraft();
    }
    private async Task PreviewAsync()
    {
        if (project == null) return;
        var edit = new Edit(operation.Text, xpath.Text, editor.Text, string.IsNullOrWhiteSpace(attribute.Text) ? null : attribute.Text);
        draft = await client.PostAsync<ChangePreview>($"/api/projects/{project.Id}/previews", new PreviewRequest(project.CurrentSha256, [edit]));
        review.Text = draft.Diff; tabs.SelectedIndex = 1; apply.Enabled = true;
        status.Text = $"Preview {draft.Id[..8]}  ·  {draft.BaseSha256[..12]} → {draft.ResultSha256[..12]}";
    }
    private async Task ApplyAsync()
    {
        if (project == null || draft == null) return;
        project = await client.PostAsync<ProjectInfo>($"/api/projects/{project.Id}/previews/{draft.Id}/apply", new ApplyRequest(draft.BaseSha256));
        InvalidateDraft(); await RefreshAsync(project.Id);
    }
    private async Task ExportAsync(string format)
    {
        if (project == null) return;
        var artifact = await client.PostAsync<Artifact>($"/api/projects/{project.Id}/exports", new ExportRequest(project.CurrentSha256, format));
        var capabilities = await client.GetAsync<JsonElement>("/api/capabilities");
        using var dialog = new SaveFileDialog { FileName = project.Name + "_edited." + artifact.Format, Filter = $"Project artifact|*.{artifact.Format}",
            InitialDirectory = capabilities.GetProperty("outputRoots")[0].GetString() };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(dialog.FileName));
        status.Text = $"Saved {dialog.FileName}  ·  {artifact.Validation}";
    }
    private async Task AuditAsync()
    {
        review.Text = JsonSerializer.Serialize(await client.GetAsync<JsonElement>("/api/audit" + (project is null ? "" : "?projectId=" + project.Id)), ProjectService.Json);
        tabs.SelectedIndex = 1;
    }

    private void ShowJson(object value)
    {
        review.Text = JsonSerializer.Serialize(value, ProjectService.Json); tabs.SelectedIndex = 1;
    }

    private async Task PrepareGraphicsAsync()
    {
        if (project is null || !FactoryTalkGraphics.IsFactoryTalk(project.Kind))
            throw new InvalidOperationException("Select an ME or SE display XML working copy first.");
        using var dialog = new GraphicsTargetDialog(project.Name);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var plan = await client.PostAsync<GraphicsImportPlan>($"/api/projects/{project.Id}/graphics-imports",
            new PrepareGraphicsImportRequest(project.CurrentSha256, dialog.ApplicationName, dialog.DisplayName, dialog.Language));
        graphicsPlanId.Text = plan.Id; ShowJson(plan);
        status.Text = $"{plan.Edition} update import prepared for {plan.ApplicationName} / {plan.DisplayName} ({plan.Language}) · native import pending";
    }

    private async Task VerifyGraphicsAsync()
    {
        if (string.IsNullOrWhiteSpace(graphicsPlanId.Text)) throw new InvalidOperationException("Prepare an import or enter its plan ID from Codex.");
        var result = await client.PostAsync<GraphicsVerification>($"/api/factorytalk/imports/{graphicsPlanId.Text.Trim()}/verify", new VerifyGraphicsImportRequest());
        ShowJson(result);
        status.Text = $"Re-export: {result.Status} · native application/language and completion must also be observed in Studio";
    }

    // Exercises the real HTTP client and renders the actual WinForms layout without displaying a window.
    public void LoadForTest(string path)
    {
        var (info, inspection, content) = Task.Run(async () =>
        {
            var info = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(path));
            var kind = FactoryTalkGraphics.IsFactoryTalk(info.Kind) ? "text" : "Routine";
            var inspection = await client.GetAsync<Inspection>($"/api/projects/{info.Id}/elements?kind={kind}");
            var component = inspection.Elements.First();
            var content = await client.GetAsync<ElementContent>($"/api/projects/{info.Id}/element?xpath={Uri.EscapeDataString(component.XPath)}");
            return (info, inspection, content);
        }).GetAwaiter().GetResult();
        suppress = true; projects.DataSource = new[] { info }; elements.DataSource = inspection.Elements.ToArray();
        if (elements.Columns["XPath"] is { } column) column.Visible = false;
        if (elements.Columns["Program"] is { } contextColumn) contextColumn.HeaderText = "Program / group";
        xpath.Text = content.XPath; editor.Text = content.Xml; project = info;
        status.Text = $"{info.Name}  ·  {info.Kind}  ·  revision {info.Revision}  ·  SHA {info.CurrentSha256[..12]}  ·  Shared service connected";
        PerformLayout();
    }
}
