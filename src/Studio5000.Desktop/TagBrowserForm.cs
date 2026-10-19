using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class TagBrowserForm : Form
{
    private readonly ServiceClient client;
    private readonly ProjectInfo? initial;
    private readonly ComboBox projects = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly TextBox search = new() { Width = 230, PlaceholderText = "Tag, program, type or description" };
    private readonly TextBox job = new() { Width = 225, PlaceholderText = "Completed upload job ID" };
    private readonly TreeView tree = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false, ShowNodeToolTips = true };
    private readonly TextBox detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new("Consolas", 10) };
    private readonly ListBox basket = new() { Dock = DockStyle.Fill };
    private readonly TextBox name = new() { Width = 180, Text = "Browser watch list" };
    private readonly NumericUpDown interval = new() { Width = 90, Minimum = 100, Maximum = 86400000, Value = 1000, Increment = 100 };
    private readonly ComboBox mode = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox target = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 54, Padding = new(8) };
    private readonly Dictionary<string, WatchTag> selected = new(StringComparer.Ordinal);
    private ProjectInfo? project;
    private TagBrowserPage? page;
    private TreeNode? controller, typesRoot;
    private readonly Dictionary<string, TreeNode> programs = new(StringComparer.Ordinal);
    private bool busy, suppress, fixture;
    public WatchStatus? CreatedWatch { get; private set; }
    private sealed record Branch(TagBrowseRequest Request);
    private sealed record More(TagBrowseRequest Request);

    public TagBrowserForm(ServiceClient client, ProjectInfo? initial)
    {
        this.client = client; this.initial = initial;
        Text = "Tag and UDT browser"; Size = new(1320, 850); MinimumSize = new(1100, 700); Font = new("Segoe UI", 10);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 90, Padding = new(8) };
        top.Controls.Add(projects); Button(top, "Open ACD / L5X…", OpenAsync); top.Controls.Add(search); Button(top, "Search", SearchAsync);
        top.SetFlowBreak(top.Controls[^1], true); top.Controls.Add(job); Button(top, "Load upload result", LoadJobAsync);
        Button(top, "Where used…", async () =>
        {
            if (project is null || page is null || tree.SelectedNode is not { } selectedNode || !metadata.TryGetValue(selectedNode, out var node) || node.TagName is null)
                throw new InvalidOperationException("Select a tag or member in the tree first.");
            var current = await client.GetAsync<ProjectInfo>($"/api/projects/{project.Id}");
            if (current.CurrentSha256 != page.SourceSha256) throw new InvalidOperationException("Snapshot changed. Refresh the browser before finding references.");
            using var references = new TagReferencesForm(client, current, node.TagName, node.ProgramName); references.ShowDialog(this);
        });
        var title = new Label { Text = "TAG BROWSER  /  CHECK TAGS OR MEMBERS TO BUILD A WATCH LIST", Dock = DockStyle.Top, Height = 46,
            Padding = new(12), BackColor = Color.FromArgb(24, 40, 61), ForeColor = Color.White };
        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(tree);
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        right.Panel1.Controls.Add(detail); right.Panel2.Controls.Add(basket);
        right.Panel2.Controls.Add(new Label { Text = "SELECTED VALUES  •  select an item and press Delete to remove", Dock = DockStyle.Top, Height = 32, Padding = new(5) });
        split.Panel2.Controls.Add(right);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 84, Padding = new(8) };
        bottom.Controls.Add(new Label { Text = "Watch name", AutoSize = true }); bottom.Controls.Add(name);
        bottom.Controls.Add(new Label { Text = "Interval ms", AutoSize = true }); bottom.Controls.Add(interval);
        mode.Items.AddRange(["Offline", "Online"]); mode.SelectedIndex = 0; bottom.Controls.Add(mode); bottom.Controls.Add(target);
        Button(bottom, "Create watch list", CreateAsync); Button(bottom, "Clear selection", () => { selected.Clear(); UpdateBasket(); Uncheck(tree.Nodes); return Task.CompletedTask; });
        Controls.Add(split); Controls.Add(bottom); Controls.Add(status); Controls.Add(top); Controls.Add(title);
        tree.BeforeExpand += async (_, e) =>
        {
            if (e.Node?.Tag is Branch branch) { e.Cancel = true; await RunAsync(async () => { await ExpandAsync(e.Node, branch.Request); e.Node.Tag = null; e.Node.Expand(); }); }
        };
        tree.NodeMouseDoubleClick += async (_, e) => { if (e.Node.Tag is More more) await RunAsync(async () => await MoreAsync(e.Node, more.Request)); };
        tree.AfterSelect += (_, e) => { if (e.Node is not null && metadata.TryGetValue(e.Node, out var node)) ShowDetail(node); };
        // Expandable nodes keep metadata in Name's companion dictionary, since Tag is their lazy branch.
        tree.AfterCheck += (_, e) => { if (!suppress && e.Node is not null) Toggle(e.Node); };
        projects.SelectedIndexChanged += async (_, _) => { if (!suppress && projects.SelectedItem is ProjectInfo p) await RunAsync(() => LoadProjectAsync(p)); };
        search.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RunAsync(SearchAsync); } };
        basket.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && basket.SelectedItem is string key) { selected.Remove(key); UpdateBasket(); Uncheck(tree.Nodes); }
        };
        Shown += async (_, _) => { split.SplitterDistance = Math.Max(500, split.Width / 2); right.SplitterDistance = right.Height / 2; if (!fixture) await RunAsync(InitializeAsync); };
    }
    private readonly Dictionary<TreeNode, TagBrowserNode> metadata = new();
    private void Button(Control parent, string text, Func<Task> action)
    { var b = new Button { Text = text, AutoSize = true }; b.Click += async (_, _) => await RunAsync(action); parent.Controls.Add(b); }
    private async Task RunAsync(Func<Task> action)
    { if (busy) return; busy = true; UseWaitCursor = true; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; UseWaitCursor = false; } }
    private async Task InitializeAsync()
    {
        var caps = await client.GetAsync<JsonElement>("/api/sdk/capabilities");
        target.Items.Add(""); foreach (var t in caps.GetProperty("controllerTargets").EnumerateArray()) target.Items.Add(t.GetProperty("id").GetString()!); target.SelectedIndex = 0;
        await RefreshProjectsAsync(initial?.Id);
    }
    private async Task RefreshProjectsAsync(string? id)
    {
        var items = (await client.GetAsync<ProjectInfo[]>("/api/projects")).Where(p => p.Kind == "logix").ToArray();
        suppress = true; projects.DataSource = items; projects.SelectedItem = items.FirstOrDefault(p => p.Id == id) ?? items.FirstOrDefault(); suppress = false;
        if (projects.SelectedItem is ProjectInfo p) await LoadProjectAsync(p); else status.Text = "Open an ACD/L5X or load a completed controller-upload job. Browsing uses saved metadata and never connects.";
    }
    private async Task OpenAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Logix project|*.acd;*.l5x" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var p = await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(dialog.FileName)); await RefreshProjectsAsync(p.Id);
    }
    private async Task LoadJobAsync()
    {
        var result = await client.GetAsync<SdkJob>("/api/sdk/jobs/" + Uri.EscapeDataString(job.Text.Trim()));
        if (result.Status != "succeeded" || result.ResultProject is null) throw new ArgumentException("Choose a successful upload job with a published Logix result.");
        await RefreshProjectsAsync(result.ResultProject.Id);
    }
    private async Task LoadProjectAsync(ProjectInfo p)
    { project = p; selected.Clear(); UpdateBasket(); await SearchAsync(); }
    private async Task SearchAsync()
    {
        if (project is null) return;
        page = await client.PostAsync<TagBrowserPage>("/api/tags/browse", new TagBrowseRequest(project.Id, Query: search.Text.Trim()));
        BuildTree(page);
    }
    private void BuildTree(TagBrowserPage value)
    {
        tree.BeginUpdate(); suppress = true; tree.Nodes.Clear(); metadata.Clear(); programs.Clear();
        controller = tree.Nodes.Add(value.Controller + "  /  controller tags");
        var programRoot = tree.Nodes.Add("Programs"); foreach (var program in value.Programs) programs.Add(program, programRoot.Nodes.Add(program));
        typesRoot = tree.Nodes.Add("UDT definitions");
        foreach (var type in value.Types.Where(t => search.Text.Length == 0 || t.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || t.Members.Any(m => m.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase))))
        {
            var node = typesRoot.Nodes.Add(type.Name); node.Tag = new Branch(new(value.ProjectId, value.SourceSha256, DataTypeName: type.Name)); node.Nodes.Add("Expand to load members");
        }
        AddTags(value); controller.Expand(); programRoot.Expand(); foreach (TreeNode node in programRoot.Nodes) node.Expand(); typesRoot.Expand();
        suppress = false; tree.EndUpdate();
        status.Text = $"Snapshot {value.SourceSha256[..12]} • {value.Total} matching tags • arrays expand one page at a time. Select a leaf to inspect it.";
        detail.Text = "Check a tag or member, then Create watch list.\r\n\r\nLists are created stopped. Offline polling reads saved data; Online polling requires the configured target and enabled controller access.\r\n\r\nUDT definitions show declared members. Built-in types without exported declarations have no inferred layout.";
    }
    private void AddTags(TagBrowserPage value)
    {
        foreach (var node in value.Nodes) AddNode(node.ProgramName is null ? controller! : programs[node.ProgramName], node);
        if (value.Offset + value.Nodes.Length < value.Total)
            tree.Nodes.Add(new TreeNode("Load more matching tags… (double-click)") { Tag = new More(new(value.ProjectId, value.SourceSha256, Query: search.Text.Trim(), Offset: value.Offset + value.Nodes.Length)) });
    }
    private TreeNode AddNode(TreeNode parent, TagBrowserNode data)
    {
        var dims = data.Dimensions.Length == 0 ? "" : "[" + string.Join(',', data.Dimensions) + "]";
        var node = parent.Nodes.Add(data.Name + "  :  " + data.DataType + dims); metadata.Add(node, data);
        node.ForeColor = data.Watchable ? Color.FromArgb(25, 55, 85) : Color.DimGray; node.ToolTipText = data.Description;
        node.Tag = data; node.Checked = selected.ContainsKey(Key(data));
        if (data.Expandable)
        {
            // Type-only expansion is rendered directly from the exported declarations below.
            if (data.TagName is not null) node.Tag = new Branch(new(page!.ProjectId, page.SourceSha256, data.ProgramName, data.TagName, data.MemberPath));
            else if (data.Dimensions.Length == 0) node.Tag = new Branch(new(page!.ProjectId, page.SourceSha256, DataTypeName: data.DataType));
            else { node.Tag = data; node.ToolTipText = "Array declaration; browse an instance tag to select indexed values."; return node; }
            node.Nodes.Add("Expand to load members");
        }
        return node;
    }
    private async Task ExpandAsync(TreeNode parent, TagBrowseRequest request)
    {
        var children = await client.PostAsync<TagBrowserPage>("/api/tags/browse", request);
        parent.Nodes.Clear(); foreach (var node in children.Nodes) AddNode(parent, node);
        if (children.Nodes.Length < children.Total) parent.Nodes.Add(new TreeNode("Load more… (double-click)") { Tag = new More(request with { Offset = children.Nodes.Length }) });
        if (metadata.TryGetValue(parent, out var data)) ShowDetail(data);
    }
    private async Task MoreAsync(TreeNode node, TagBrowseRequest request)
    {
        var next = await client.PostAsync<TagBrowserPage>("/api/tags/browse", request); var parent = node.Parent;
        node.Remove(); if (parent is null) AddTags(next);
        else
        {
            foreach (var child in next.Nodes) AddNode(parent, child);
            if (next.Offset + next.Nodes.Length < next.Total) parent.Nodes.Add(new TreeNode("Load more… (double-click)") { Tag = new More(request with { Offset = next.Offset + next.Nodes.Length }) });
        }
    }
    private static string Key(TagBrowserNode node) => (node.ProgramName is null ? "Controller" : "Program:" + node.ProgramName) + "/" + node.TagName + (string.IsNullOrEmpty(node.MemberPath) ? "" : "." + node.MemberPath);
    private void ShowDetail(TagBrowserNode data) => detail.Text = JsonSerializer.Serialize(data, ProjectService.Json);
    private void Toggle(TreeNode node)
    {
        if (!metadata.TryGetValue(node, out var data) || !data.Watchable) { suppress = true; node.Checked = false; suppress = false; return; }
        if (node.Checked) selected[Key(data)] = new("value" + (selected.Count + 1), data.TagName!, data.ProgramName, data.MemberPath); else selected.Remove(Key(data));
        UpdateBasket(); ShowDetail(data);
    }
    private void UpdateBasket() { basket.DataSource = selected.Keys.ToArray(); }
    private void Uncheck(TreeNodeCollection nodes)
    { suppress = true; foreach (TreeNode node in nodes) { node.Checked = metadata.TryGetValue(node, out var data) && selected.ContainsKey(Key(data)); Uncheck(node.Nodes); } suppress = false; }
    private async Task CreateAsync()
    {
        if (project is null || page is null) throw new ArgumentException("Open a Logix snapshot first.");
        var tags = selected.Values.ToArray();
        if (tags.Length == 0 && tree.SelectedNode is { } node && metadata.TryGetValue(node, out var data) && data.Watchable) tags = [new("value1", data.TagName!, data.ProgramName, data.MemberPath)];
        tags = tags.Select((t, i) => t with { Key = "value" + (i + 1) }).ToArray();
        CreatedWatch = await client.PostAsync<WatchStatus>("/api/tags/watch", new BrowserWatchRequest(project.Id, page.SourceSha256, name.Text.Trim(), tags, mode.Text, string.IsNullOrWhiteSpace(target.Text) ? null : target.Text, (int)interval.Value));
        DialogResult = DialogResult.OK; Close();
    }
    public void LoadForTest(TagBrowserPage value)
    {
        fixture = true; page = value; BuildTree(value);
        foreach (var item in value.Nodes.Where(n => n.Watchable).Take(2)) selected[Key(item)] = new("value" + (selected.Count + 1), item.TagName!, item.ProgramName, item.MemberPath);
        UpdateBasket(); if (value.Nodes.FirstOrDefault() is { } first) ShowDetail(first);
    }
}
