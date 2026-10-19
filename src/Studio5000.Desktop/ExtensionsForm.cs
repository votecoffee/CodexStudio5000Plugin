using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

namespace Studio5000.Desktop;

public sealed class ExtensionsForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly ComboBox modules = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDown, DisplayMember = "Id" };
    private readonly ComboBox operations = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly TextBox version = new() { Width = 130 };
    private readonly TextBox jobId = new() { Width = 270, PlaceholderText = "Extension job ID" };
    private readonly Label description = new() { Dock = DockStyle.Top, Height = 90, Padding = new(8), AutoEllipsis = true };
    private readonly TableLayoutPanel fields = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new(6) };
    private readonly TextBox result = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        Font = new("Consolas", 10), WordWrap = false };
    private readonly ToolStripStatusLabel status = new() { Text = "Refresh the live catalog. Extension reloads do not restart Codex." };
    private readonly Button execute = new() { Text = "Run operation", AutoSize = true };
    private readonly CheckBox background = new() { Text = "Run as pinned background job", AutoSize = true };
    private readonly Dictionary<string, (TextBox Editor, JsonElement Schema, bool Required)> inputs = new();
    private ExtensionSummary? module;
    private ExtensionOperation? operation;
    private string? preferredOperation;
    private readonly JsonObject? initialArguments;
    private bool busy, loading, selfTest;
    public ExtensionsForm(ServiceClient client, string? preferredOperation = null, JsonObject? initialArguments = null)
    {
        this.client = client; this.preferredOperation = preferredOperation; this.initialArguments = initialArguments;
        Text = preferredOperation is null ? "Rockwell extensions — live catalog and reload" : "Rockwell workflow — " + preferredOperation; Size = new(1400, 930); MinimumSize = new(1080, 740); Font = new("Segoe UI", 10);
        var heading = new Label { Text = "EXTENSIONS  /  LIVE OPERATIONS", Dock = DockStyle.Top, Height = 52, Padding = new(14),
            BackColor = Color.FromArgb(24, 40, 61), ForeColor = Color.White, Font = new("Segoe UI", 13, FontStyle.Bold) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(7) };
        Button(toolbar, "Refresh catalog", RefreshAsync); toolbar.Controls.Add(modules);
        toolbar.Controls.Add(new Label { Text = "Installed version", AutoSize = true, Padding = new(5, 7, 0, 0) }); toolbar.Controls.Add(version);
        Button(toolbar, "Reload extension", ReloadAsync);
        var jobs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(7) };
        jobs.Controls.Add(jobId); Button(jobs, "Read job", ReadJobAsync); Button(jobs, "List jobs", async () => Show(await client.GetAsync<JsonElement>("/api/extensions/jobs")));
        Button(jobs, "Cancel job", async () => Show(await client.PostAsync<ExtensionJob>($"/api/extensions/jobs/{jobId.Text.Trim()}/cancel", new { })));
        var layout = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 670 };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scroll.Controls.Add(fields);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(7) }; actions.Controls.Add(execute); actions.Controls.Add(background);
        layout.Panel1.Padding = new(8); layout.Panel1.Controls.Add(scroll); layout.Panel1.Controls.Add(description); layout.Panel1.Controls.Add(operations); layout.Panel1.Controls.Add(actions);
        layout.Panel2.Padding = new(8); layout.Panel2.Controls.Add(result);
        var boundary = new Label { Text = "Reload validates installed local code. Existing calls keep their version. Controller actions still require exact authorization and configured scope.",
            Dock = DockStyle.Bottom, Height = 36, Padding = new(8), BackColor = Color.FromArgb(255, 245, 220), ForeColor = Color.FromArgb(120, 65, 0) };
        var statusBar = new StatusStrip(); statusBar.Items.Add(status);
        Controls.Add(layout); Controls.Add(jobs); Controls.Add(toolbar); Controls.Add(heading); Controls.Add(boundary); Controls.Add(statusBar);
        fields.ColumnStyles.Add(new(SizeType.Percent, 30)); fields.ColumnStyles.Add(new(SizeType.Percent, 70));
        modules.SelectedIndexChanged += (_, _) => { if (!loading) SelectModule(); };
        modules.TextUpdate += (_, _) =>
        {
            if (loading) return;
            module = null; operation = null; operations.DataSource = null; execute.Enabled = false;
            description.Text = "Enter an installed extension ID and version, then reload to add it to the live catalog.";
        };
        operations.SelectedIndexChanged += (_, _) => { if (!loading) SelectOperation(); };
        execute.Click += async (_, _) => await Run(ExecuteAsync);
        Shown += async (_, _) => { layout.SplitterDistance = Width / 2; if (!selfTest) await Run(RefreshAsync); };
    }
    private void Button(Control parent, string text, Func<Task> action)
    { var button = new Button { Text = text, AutoSize = true }; button.Click += async (_, _) => await Run(action); parent.Controls.Add(button); }
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; Enabled = false; UseWaitCursor = true;
        try { await action(); }
        catch (Exception ex) { status.Text = ex.Message; MessageBox.Show(this, ex.Message, "Extensions", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Enabled = true; UseWaitCursor = false; busy = false; }
    }
    private void Show<T>(T value) => result.Text = JsonSerializer.Serialize(value, ProjectService.Json);
    private async Task RefreshAsync()
    {
        var catalog = await client.GetAsync<ExtensionCatalog>("/api/extensions"); var id = module?.Id;
        loading = true; modules.DataSource = catalog.Extensions.Where(x => x.State == "active").ToArray();
        if (preferredOperation is not null) id = ((ExtensionSummary[])modules.DataSource).FirstOrDefault(x => x.Operations.Any(o => o.Name == preferredOperation))?.Id;
        if (id is not null) modules.SelectedItem = ((ExtensionSummary[])modules.DataSource).FirstOrDefault(x => x.Id == id);
        loading = false; SelectModule();
        status.Text = $"{catalog.Extensions.Count(x => x.State == "active")} active extensions • {catalog.Extensions.Count(x => x.State == "draining")} draining versions • protocol {catalog.ProtocolVersion}";
        if (catalog.LoadErrors.Length != 0) Show(catalog.LoadErrors);
    }
    private void SelectModule()
    {
        module = modules.SelectedItem as ExtensionSummary;
        loading = true; operations.DataSource = module?.Operations; loading = false;
        if (preferredOperation is not null && module?.Operations.FirstOrDefault(o => o.Name == preferredOperation) is { } selected)
        { loading = true; operations.SelectedItem = selected; loading = false; }
        version.Text = module?.Version ?? ""; SelectOperation();
    }
    private void SelectOperation()
    {
        operation = operations.SelectedItem as ExtensionOperation; inputs.Clear();
        foreach (Control control in fields.Controls.Cast<Control>().ToArray()) control.Dispose(); fields.Controls.Clear(); fields.RowStyles.Clear(); fields.RowCount = 0;
        execute.Enabled = operation is not null;
        if (operation is null) { description.Text = "No active operation. Build/install an extension package, then reload its ID/version."; return; }
        execute.Text = "Run " + operation.Effect + " operation";
        description.Text = operation.Description + "\r\nEffect: " + operation.Effect + " • generation: " + module!.Generation[..12];
        if (!operation.InputSchema.TryGetProperty("properties", out var properties)) return;
        var required = operation.InputSchema.TryGetProperty("required", out var req) ? req.EnumerateArray().Select(x => x.GetString()).ToArray() : [];
        foreach (var property in properties.EnumerateObject())
        {
            var kind = Kind(property.Value); var complex = kind is "object" or "array"; var needed = required.Contains(property.Name);
            var editor = new TextBox { Dock = DockStyle.Fill, Multiline = complex, ScrollBars = complex ? ScrollBars.Both : ScrollBars.None,
                Font = complex ? new("Consolas", 10) : Font, Height = complex ? 190 : 30, Margin = new(4, 6, 4, 6) };
            if (complex && needed) editor.Text = Template(property.Value).ToJsonString(new() { WriteIndented = true });
            if (operation.Name == preferredOperation && initialArguments?.TryGetPropertyValue(property.Name, out var value) == true && value is not null)
                editor.Text = kind == "string" ? value.GetValue<string>() : value.ToJsonString(new() { WriteIndented = true });
            fields.RowCount++; fields.RowStyles.Add(new(SizeType.AutoSize));
            fields.Controls.Add(new Label { Text = property.Name + (needed ? " *" : " (optional)") + "\r\n" + (kind == "string" ? "plain text" : "JSON " + kind), AutoSize = true, Padding = new(0, 8, 0, 0) }, 0, fields.RowCount - 1);
            fields.Controls.Add(editor, 1, fields.RowCount - 1); inputs.Add(property.Name, (editor, property.Value, needed));
        }
        if (operation.Name == preferredOperation) preferredOperation = null;
    }
    private static string Kind(JsonElement schema) => !schema.TryGetProperty("type", out var type) ? "object" :
        type.ValueKind == JsonValueKind.String ? type.GetString()! : type.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x != "null") ?? "null";
    private static JsonNode Template(JsonElement schema)
    {
        var kind = Kind(schema);
        if (kind == "array") return new JsonArray();
        if (kind == "boolean") return JsonValue.Create(false)!;
        if (kind is "integer" or "number") return JsonValue.Create(0)!;
        if (kind == "string") return JsonValue.Create("")!;
        var result = new JsonObject();
        if (schema.TryGetProperty("required", out var required) && schema.TryGetProperty("properties", out var properties))
            foreach (var key in required.EnumerateArray()) result[key.GetString()!] = Template(properties.GetProperty(key.GetString()!));
        return result;
    }
    private async Task ExecuteAsync()
    {
        if (module is null || operation is null) return;
        var args = BuildArguments(); ExtensionSchema.Validate(operation.InputSchema, args);
        var call = new ExtensionCall(module.Id, operation.Name, module.Generation, args);
        if (background.Checked)
        {
            var job = await client.PostAsync<ExtensionJob>("/api/extensions/jobs", new ExtensionJobRequest(operation.Effect, call)); jobId.Text = job.Id; Show(job);
        }
        else Show(await client.PostAsync<JsonElement>("/api/extensions/invoke/" + operation.Effect, call));
    }
    private JsonElement BuildArguments()
    {
        var arguments = new JsonObject();
        foreach (var input in inputs)
        {
            var (editor, schema, required) = input.Value;
            if (!required && string.IsNullOrWhiteSpace(editor.Text)) continue;
            arguments[input.Key] = Kind(schema) == "string" ? JsonValue.Create(editor.Text) : JsonNode.Parse(editor.Text);
        }
        return JsonSerializer.SerializeToElement(arguments);
    }
    internal async Task<string> ValidatePreparedForTestAsync()
    { selfTest = true; await RefreshAsync(); ExtensionSchema.Validate(operation!.InputSchema, BuildArguments()); return operation.Name; }
    private async Task ReloadAsync()
    {
        var id = modules.Text.Trim(); if (id.Length == 0) return;
        var loaded = await client.PostAsync<ExtensionSummary>("/api/extensions/reload", new ExtensionReloadRequest(id, version.Text.Trim(), module?.Id == id ? module.Generation : null));
        module = loaded;
        await RefreshAsync(); Show(loaded);
    }
    private async Task ReadJobAsync() { if (!string.IsNullOrWhiteSpace(jobId.Text)) Show(await client.GetAsync<ExtensionJob>("/api/extensions/jobs/" + jobId.Text.Trim())); }
    public void LoadForTest()
    {
        selfTest = true; loading = true;
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { projectId = new { type = "string" }, file = new { type = "string" } }, required = new[] { "projectId", "file" }, additionalProperties = false });
        var operation = new ExtensionOperation("viewdesigner_screen_summary", "Summarize objects, properties and bindings in an exported HMI file. Loaded from the extension without adding an MCP tool.", "read", schema);
        modules.DataSource = new[] { new ExtensionSummary("viewdesigner", "0.7.0", new string('a', 32), new string('b', 64), [operation], 0, "active") };
        loading = false; SelectModule(); inputs["projectId"].Editor.Text = new string('c', 32); inputs["file"].Editor.Text = "User-Defined Screens/Overview.hmi";
        Show(new { path = "User-Defined Screens/Overview.hmi", objects = 7, properties = 22, bindings = new[] { "::LGX.Process.Count" }, generation = new string('a', 32) });
        status.Text = "4 active extensions • 0 draining versions • protocol 1";
    }
}
