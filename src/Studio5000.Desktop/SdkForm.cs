using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class SdkForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly ProjectInfo? project;
    private readonly ComboBox methods = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox request = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly TextBox result = new() { Multiline = true, Dock = DockStyle.Fill, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private readonly TextBox jobId = new() { Width = 250, PlaceholderText = "Job ID from GUI or Codex" };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(8) };
    private SdkMethodInfo[] catalog = [];
    private bool busy;
    private bool testLoaded;
    private string? sdkServiceRequestId;

    public SdkForm(ServiceClient client, ProjectInfo? project)
    {
        this.client = client; this.project = project;
        Text = "Logix Designer SDK — shared service jobs"; Size = new(1280, 850); MinimumSize = new(1000, 650);
        Font = new("Segoe UI", 10);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(8) };
        toolbar.Controls.Add(methods);
        Button(toolbar, "Add operation", AddOperationAsync);
        Button(toolbar, "Use selected project", UseProjectAsync);
        Button(toolbar, "Choose source…", ChooseSourceAsync);
        Button(toolbar, "Catalog", async () => Show(await client.GetAsync<JsonElement>("/api/sdk/capabilities")));
        Button(toolbar, "Coverage", async () => Show(await client.GetAsync<JsonElement>("/api/sdk/coverage")));
        Button(toolbar, "Online edit…", PrepareOnlineEditAsync);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(8, 3, 8, 3) };
        Button(actions, "Run job", SubmitAsync); actions.Controls.Add(jobId);
        Button(actions, "Refresh job", RefreshAsync);
        Button(actions, "Cancel job", async () => Show(await client.PostAsync<SdkJob>("/api/sdk/jobs/" + jobId.Text.Trim() + "/cancel", new { })));
        Button(actions, "List jobs", async () => Show(await client.GetAsync<SdkJob[]>("/api/sdk/jobs")));
        Button(actions, "Save output…", SaveAsync);
        var serviceActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new(8, 3, 8, 3) };
        Button(serviceActions, "SDK service status", async () => Show(await client.GetAsync<SdkServiceStatus>("/api/sdk/service")));
        Button(serviceActions, "Start SDK service", EnsureSdkServiceAsync);
        serviceActions.Controls.Add(new Label { Text = "Starts when stopped; a running service stays running.", AutoSize = true, Padding = new(8) });
        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(request); split.Panel1.Controls.Add(new Label { Text = "REQUEST  /  exact SDK methods and JSON arguments", Dock = DockStyle.Top, Height = 30 });
        split.Panel2.Controls.Add(result); split.Panel2.Controls.Add(new Label { Text = "JOB RESULT  /  steps, diagnostics and artifacts", Dock = DockStyle.Top, Height = 30 });
        var note = new Label { Text = "Calls run in order in one SDK session. Choose an ACD for native work, or use a managed L5X revision. Results create new files and a new project.", Dock = DockStyle.Top, Height = 43, Padding = new(8) };
        Controls.Add(split); Controls.Add(status); Controls.Add(note); Controls.Add(actions); Controls.Add(serviceActions); Controls.Add(toolbar);
        request.Text = JsonSerializer.Serialize(new SdkRunRequest([]), ProjectService.Json);
        Shown += async (_, _) =>
        {
            split.SplitterDistance = split.Width / 2;
            request.Select(0, 0); result.Select(0, 0); methods.Focus();
            if (!testLoaded) await RunAsync(InitializeAsync);
        };
    }
    private void Button(Control parent, string label, Func<Task> action)
    { var button = new Button { Text = label, AutoSize = true }; button.Click += async (_, _) => await RunAsync(action); parent.Controls.Add(button); }
    private async Task RunAsync(Func<Task> action)
    { if (busy) return; busy = true; UseWaitCursor = true; try { await action(); } catch (Exception ex) { result.Text = ex.Message; status.Text = "Operation failed; details are shown in the result pane."; } finally { busy = false; UseWaitCursor = false; } }
    private SdkRunRequest Request() => JsonSerializer.Deserialize<SdkRunRequest>(request.Text, ProjectService.Json) ?? throw new ArgumentException("Enter a request.");
    private void Request(SdkRunRequest value) => request.Text = JsonSerializer.Serialize(value, ProjectService.Json);
    private void Show<T>(T value) => result.Text = JsonSerializer.Serialize(value, ProjectService.Json);
    private async Task EnsureSdkServiceAsync()
    {
        sdkServiceRequestId ??= Guid.NewGuid().ToString("N");
        var receipt = await client.PostAsync<SdkServiceReceipt>("/api/sdk/service/ensure-running", new SdkServiceEnsureRequest(sdkServiceRequestId));
        Show(receipt); status.Text = receipt.Message;
        if (receipt.Outcome is not ("dispatching" or "uncertain")) sdkServiceRequestId = null;
    }
    public async Task InitializeAsync()
    {
        var capabilities = await client.GetAsync<JsonElement>("/api/sdk/capabilities");
        BindCapabilities(capabilities);
    }
    private void BindCapabilities(JsonElement capabilities)
    {
        catalog = capabilities.GetProperty("methods").Deserialize<SdkMethodInfo[]>(ProjectService.Json)!;
        methods.DataSource = catalog; methods.DisplayMember = "Name";
        methods.SelectedItem = catalog.Single(m => m.Name == "CreateNewProjectAsync");
        status.Text = $"SDK 2.1.974 · {catalog.Length} operations · controller operations enabled: {capabilities.GetProperty("controllerOperationsEnabled")}";
    }
    public void LoadForTest()
    {
        var data = Task.Run(async () => (await client.GetAsync<JsonElement>("/api/sdk/capabilities"), await client.GetAsync<JsonElement>("/api/sdk/coverage"))).GetAwaiter().GetResult();
        BindCapabilities(data.Item1); Show(data.Item2); testLoaded = true;
        methods.SelectedItem = catalog.Single(m => m.Name == "CreateNewProjectAsync"); AddOperationAsync().GetAwaiter().GetResult();
        methods.SelectedItem = catalog.Single(m => m.Name == "BuildAsync"); AddOperationAsync().GetAwaiter().GetResult();
    }
    private Task AddOperationAsync()
    {
        var method = (SdkMethodInfo)methods.SelectedItem!;
        var arguments = new Dictionary<string, JsonElement>();
        foreach (var p in method.Parameters)
        {
            object? value = p.Type switch
            {
                "System.String" => p.Optional ? p.DefaultValue ?? "" : p.Name switch { "controllerName" => "CodexDemo", "processorTypeName" => "1756-L81E", "savePath" => "export.acd", "filePath" => "component.l5x", "xmlFileToImport" => "component", _ => "" },
                "System.Boolean" => p.DefaultValue == "True",
                "System.Byte[]" => "",
                _ when p.Type.Contains('+') => p.DefaultValue ?? SdkCatalog.Surface.Enums.Single(e => e.Name == p.Type.Split('+')[1]).Values[0],
                _ => p.Name is "majorRev" or "majorRevision" or "destinationRevision" ? 38 : 0
            };
            if (p.Name is "projectFilePath" or "commPath" || method.Name == "ConvertAsync" && p.Name == "projectPath") continue;
            arguments[p.Name] = JsonSerializer.SerializeToElement(value);
        }
        var current = Request(); Request(current with { Calls = [..current.Calls, new(method.Name, arguments)] });
        return Task.CompletedTask;
    }
    private Task UseProjectAsync()
    {
        if (project is null || project.Kind != "logix") throw new ArgumentException("Select a Logix managed project first.");
        Request(Request() with { ProjectId = project.Id, ExpectedSha256 = project.CurrentSha256, SourcePath = null, SourceArtifactId = null });
        return Task.CompletedTask;
    }
    private async Task ChooseSourceAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "SDK project|*.acd;*.l5x;*.l5k" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var info = await client.PostAsync<JsonElement>("/api/sdk/source", new RegisterRequest(dialog.FileName));
        Request(Request() with { ProjectId = null, ExpectedSha256 = info.GetProperty("sha256").GetString(), SourcePath = dialog.FileName, SourceArtifactId = null });
    }
    private Task PrepareOnlineEditAsync()
    {
        using var dialog = new OnlineEditDialog(client);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Plan is { } plan)
        {
            Request(plan.SdkRequest); Show(plan);
            status.Text = "Online-edit plan prepared only. Native test/assemble observations remain pending; Run job is a separate action.";
        }
        return Task.CompletedTask;
    }
    private async Task SubmitAsync()
    {
        var job = await client.PostAsync<SdkJob>("/api/sdk/jobs", Request()); jobId.Text = job.Id; Show(job);
        // Erase entered passwords from the form after submission; the service never persists the raw request.
        var current = Request(); foreach (var call in current.Calls) foreach (var key in call.Arguments.Keys.Where(SdkCatalog.IsSecret).ToArray()) call.Arguments[key] = JsonSerializer.SerializeToElement("");
        Request(current); status.Text = "Job queued. Refresh to observe completion; cancellation does not roll back controller effects.";
    }
    private async Task RefreshAsync()
    { var job = await client.GetAsync<SdkJob>("/api/sdk/jobs/" + jobId.Text.Trim()); Show(job); status.Text = $"{job.Status} · {job.Steps.Length} completed/attempted calls · {job.Artifacts.Length} output files"; }
    private async Task SaveAsync()
    {
        var job = await client.GetAsync<SdkJob>("/api/sdk/jobs/" + jobId.Text.Trim());
        if (job.Artifacts.Length == 0) throw new ArgumentException("This job has no published artifacts.");
        using var chooser = new WorkspaceForm { Text = "Choose SDK output", Size = new(900, 280), StartPosition = FormStartPosition.CenterParent };
        var list = new ListBox { Dock = DockStyle.Fill, DataSource = job.Artifacts, DisplayMember = "Validation" };
        var select = new Button { Text = "Save selected output…", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK, Height = 36 };
        chooser.Controls.Add(list); chooser.Controls.Add(select); chooser.AcceptButton = select;
        if (chooser.ShowDialog(this) != DialogResult.OK || list.SelectedItem is not Artifact artifact) return;
        var caps = await client.GetAsync<JsonElement>("/api/capabilities");
        using var dialog = new SaveFileDialog { FileName = "sdk-output." + artifact.Format, Filter = "SDK output|*." + artifact.Format, InitialDirectory = caps.GetProperty("outputRoots")[0].GetString() };
        if (dialog.ShowDialog(this) == DialogResult.OK) Show(await client.PostAsync<SavedArtifact>("/api/artifacts/" + artifact.Id + "/save", new SaveArtifactRequest(dialog.FileName)));
    }
}
