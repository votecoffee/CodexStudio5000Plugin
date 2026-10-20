using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Desktop;

public sealed class SettingsForm : WorkspaceForm
{
    private readonly ServiceClient client;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage jsonPage = new("Advanced JSON");
    private readonly TextBox json = Code(), effective = Code();
    private readonly CheckBox controller = Check("Enable controller operations"), anyController = Check("Allow any explicit SDK communications path (path:...)");
    private readonly CheckBox anySource = Check("Allow source files anywhere this Windows user can read"), anyOutput = Check("Allow new exports anywhere this Windows user can write");
    private readonly TextBox sources = Lines(), outputs = Lines();
    private readonly ListBox targets = new() { Dock = DockStyle.Fill, DisplayMember = "Id" };
    private readonly Dictionary<string, TextBox> paths = new();
    private readonly Dictionary<string, NumericUpDown> numbers = new();
    private readonly Label state = new() { Dock = DockStyle.Top, Height = 72, Padding = new(12), BackColor = Color.FromArgb(232, 240, 248) };
    private readonly ListBox checks = new() { Dock = DockStyle.Fill };
    private JsonObject document = new();
    private ServiceSettingsSnapshot? snapshot;
    private bool loading, busy, fixture;
    private static TextBox Code() => new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private static TextBox Lines() => new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top, Padding = new(4) };

    public SettingsForm(ServiceClient client)
    {
        this.client = client; Text = "Settings — local access and service configuration"; Size = new(1200, 900); MinimumSize = new(1000, 740); Font = new("Segoe UI", 10);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(8) };
        Button(toolbar, "Reload saved", ReloadAsync); Button(toolbar, "Broad local access", BroadAsync); Button(toolbar, "Save for next restart", SaveAsync);
        var accessPage = new TabPage("Access"); var access = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new(12) };
        access.Controls.Add(controller); access.Controls.Add(anyController); access.Controls.Add(anySource); access.Controls.Add(anyOutput);
        access.Controls.Add(new Label { Text = "Allowed source directories (one per line; preferred roots when unrestricted)", AutoSize = true }); access.Controls.Add(sources);
        access.Controls.Add(new Label { Text = "Output directories (one per line; preferred save locations when unrestricted)", AutoSize = true }); access.Controls.Add(outputs);
        access.Controls.Add(new Label { Text = "Codex and other MCP clients manage their own approval policies. An explicit target and operation are still selected for each controller request.", Dock = DockStyle.Fill });
        for (var i = 0; i < 9; i++) access.RowStyles.Add(i is 5 or 7 ? new(SizeType.Percent, 50) : new(SizeType.Absolute, i == 8 ? 60 : 38));
        accessPage.Controls.Add(access);
        var targetPage = new TabPage("Controller targets"); var targetActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(8) };
        Button(targetActions, "Add target…", () => EditTarget(false)); Button(targetActions, "Edit selected…", () => EditTarget(true));
        Button(targetActions, "Remove selected", () => { if (targets.SelectedIndex >= 0) targets.Items.RemoveAt(targets.SelectedIndex); return Task.CompletedTask; });
        targetPage.Controls.Add(targets); targetPage.Controls.Add(targetActions);
        var runtimePage = new TabPage("Runtime and limits"); var runtime = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new(12) };
        runtime.ColumnStyles.Add(new(SizeType.Absolute, 265)); runtime.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var (key, label) in new[] { ("ServiceUrl", "Service HTTP loopback URL"), ("StateDirectory", "Managed state directory"), ("NativeBridgePath", "SDK bridge executable (optional)"),
            ("CipBridgePath", "CIP reader executable (optional)"), ("FactoryTalkSchemaDirectory", "FactoryTalk schema directory"), ("FactoryTalkStudioPath", "FactoryTalk Studio executable"),
            ("ViewDesignerPath", "View Designer executable"), ("ExtensionsDirectory", "Extension packages directory"), ("ExtensionWorkerPath", "Extension worker executable") })
        { var field = new TextBox { Dock = DockStyle.Fill }; paths.Add(key, field); Row(runtime, label, field); }
        foreach (var (key, label, maximum) in new[] { ("NativeTimeoutSeconds", "Native job timeout (seconds)", 86400), ("ExtensionTimeoutSeconds", "Extension timeout (seconds)", 1800),
            ("MaximumConcurrentMutations", "Concurrent mutations", 32), ("MaximumQueuedMutations", "Queued mutations", 4096) })
        { var field = new NumericUpDown { Minimum = 1, Maximum = maximum, Dock = DockStyle.Fill }; numbers.Add(key, field); Row(runtime, label, field); }
        runtimePage.Controls.Add(runtime); jsonPage.Controls.Add(json);
        var effectivePage = new TabPage("Running service"); effective.ReadOnly = true; effectivePage.Controls.Add(effective);
        var checksPage = new TabPage("Always enforced"); checksPage.Controls.Add(checks);
        tabs.TabPages.AddRange([accessPage, targetPage, runtimePage, jsonPage, effectivePage, checksPage]);
        tabs.Selecting += (_, e) => { if (!loading && tabs.SelectedTab == jsonPage) try { Fill(Parse(json.Text)); } catch (Exception ex) { e.Cancel = true; state.Text = ex.Message; } };
        tabs.SelectedIndexChanged += (_, _) => { if (!loading && tabs.SelectedTab == jsonPage) json.Text = ReadFields().ToJsonString(ServiceSettingsStore.Json); };
        var footer = new Label { Text = "Save creates a private backup and stages settings for the next service start. Active polling and jobs continue unchanged.", Dock = DockStyle.Bottom, Height = 42, Padding = new(10) };
        Controls.Add(tabs); Controls.Add(state); Controls.Add(toolbar); Controls.Add(footer);
        Shown += async (_, _) => { if (!fixture) await Run(ReloadAsync); };
    }
    private static void Row(TableLayoutPanel table, string label, Control field)
    { var row = table.RowCount++; table.RowStyles.Add(new(SizeType.Absolute, 40)); table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); table.Controls.Add(field, 1, row); }
    private void Button(Control parent, string title, Func<Task> action)
    { var button = new Button { Text = title, AutoSize = true }; button.Click += async (_, _) => await Run(action); parent.Controls.Add(button); }
    private async Task Run(Func<Task> action)
    { if (busy) return; busy = true; UseWaitCursor = true; try { await action(); } catch (Exception ex) { state.Text = ex.Message; } finally { busy = false; UseWaitCursor = false; } }
    private static JsonObject Parse(string text) => JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("Settings must be a JSON object.");
    private void Fill(JsonObject next)
    {
        var options = next["Studio5000"]?.Deserialize<ServiceOptions>(ServiceSettingsStore.Json) ?? throw new ArgumentException("Studio5000 settings are required.");
        loading = true;
        try
        {
            document = next; controller.Checked = options.EnableControllerOperations; anyController.Checked = options.AllowAnyControllerPath;
            anySource.Checked = options.AllowAnySourcePath; anyOutput.Checked = options.AllowAnyOutputPath;
            sources.Lines = options.AllowedRoots; outputs.Lines = options.OutputRoots; targets.Items.Clear(); targets.Items.AddRange(options.ControllerTargets);
            foreach (var p in paths) p.Value.Text = p.Key == "ServiceUrl" ? next["ServiceUrl"]?.GetValue<string>() : (string?)typeof(ServiceOptions).GetProperty(p.Key)!.GetValue(options);
            foreach (var p in numbers)
            {
                var value = (int)typeof(ServiceOptions).GetProperty(p.Key)!.GetValue(options)!;
                if (value < p.Value.Minimum || value > p.Value.Maximum) throw new ArgumentException(p.Key + " is outside its supported range.");
                p.Value.Value = value;
            }
            json.Text = next.ToJsonString(ServiceSettingsStore.Json);
        }
        finally { loading = false; }
    }
    private JsonObject ReadFields()
    {
        var next = (JsonObject)document.DeepClone(); var options = next["Studio5000"]!.Deserialize<ServiceOptions>(ServiceSettingsStore.Json)!;
        options.EnableControllerOperations = controller.Checked; options.AllowAnyControllerPath = anyController.Checked;
        options.AllowAnySourcePath = anySource.Checked; options.AllowAnyOutputPath = anyOutput.Checked;
        options.AllowedRoots = sources.Lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray(); options.OutputRoots = outputs.Lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
        options.ControllerTargets = targets.Items.Cast<ControllerTarget>().ToArray(); next["ServiceUrl"] = paths["ServiceUrl"].Text.Trim();
        foreach (var p in paths.Where(p => p.Key != "ServiceUrl")) typeof(ServiceOptions).GetProperty(p.Key)!.SetValue(options,
            string.IsNullOrWhiteSpace(p.Value.Text) && p.Key is "NativeBridgePath" or "CipBridgePath" ? null : p.Value.Text.Trim());
        foreach (var p in numbers) typeof(ServiceOptions).GetProperty(p.Key)!.SetValue(options, (int)p.Value.Value);
        next["Studio5000"] = JsonSerializer.SerializeToNode(options, ServiceSettingsStore.Json); return next;
    }
    private JsonElement Draft() => JsonSerializer.SerializeToElement(tabs.SelectedTab == jsonPage ? Parse(json.Text) : ReadFields(), ServiceSettingsStore.Json);
    private Task EditTarget(bool edit)
    {
        var index = targets.SelectedIndex; if (edit && index < 0) return Task.CompletedTask;
        using var dialog = new ControllerTargetDialog(edit ? (ControllerTarget)targets.Items[index] : null);
        if (dialog.ShowDialog(this) == DialogResult.OK) { if (edit) targets.Items[index] = dialog.Target!; else targets.Items.Add(dialog.Target!); }
        return Task.CompletedTask;
    }
    private void LoadSnapshot(ServiceSettingsSnapshot next)
    {
        snapshot = next; Fill(Parse(next.Saved.GetRawText())); effective.Text = JsonSerializer.Serialize(next.Effective, ServiceSettingsStore.Json);
        checks.Items.Clear(); checks.Items.AddRange(next.EnforcedChecks);
        state.Text = (next.RestartRequired ? "SAVED / RESTART REQUIRED: " + string.Join(", ", next.ChangedSettings) : "Saved settings match the running service.") + "\n" + next.ConfigurationPath;
    }
    private async Task ReloadAsync() => LoadSnapshot(await client.GetAsync<ServiceSettingsSnapshot>("/api/settings"));
    private async Task BroadAsync() { var proposed = await client.PostAsync<JsonElement>("/api/settings/broad-local-preview", Draft()); Fill(Parse(proposed.GetRawText())); state.Text = "Broad local access prepared. Save to stage it; nothing has changed in the running service."; }
    private async Task SaveAsync()
    {
        if (snapshot is null) throw new InvalidOperationException("Reload saved settings first.");
        var result = await client.PostAsync<ServiceSettingsSaveResult>("/api/settings", new ServiceSettingsSaveRequest(snapshot.Revision, Draft())); LoadSnapshot(result.Settings);
    }
    public async Task<string[]> ExerciseOfflineWorkflowForTestAsync()
    {
        fixture = true; await ReloadAsync(); var original = snapshot!;
        try
        {
            controller.Checked = false; anyController.Checked = false; anySource.Checked = false; anyOutput.Checked = false;
            targets.Items.Add(new ControllerTarget("generated", @"AB_ETH-1\192.0.2.10\Backplane\0", new(["GoOnlineAsync"], "Test", "main")));
            await BroadAsync();
            if (!controller.Checked || !anyController.Checked || !anySource.Checked || !anyOutput.Checked || targets.Items.Cast<ControllerTarget>().Any(t => t.Policy is not null)) throw new Exception("Broad preset did not populate access fields and unrestricted policies.");
            await SaveAsync();
            if (snapshot?.RestartRequired != true || snapshot.Effective.GetProperty("Studio5000").GetProperty("EnableControllerOperations").GetBoolean()) throw new Exception("Settings unexpectedly changed the running service.");
        }
        finally
        {
            var current = await client.GetAsync<ServiceSettingsSnapshot>("/api/settings");
            await client.PostAsync<ServiceSettingsSaveResult>("/api/settings", new ServiceSettingsSaveRequest(current.Revision, original.Saved)); await ReloadAsync();
        }
        if (snapshot!.RestartRequired) throw new Exception("Restoring original settings did not clear pending changes.");
        await BroadAsync(); // Render the proposed profile; do not save it again.
        return ["Broad local preset populates all four access controls and clears target restrictions", "GUI stages settings through shared Core without enabling the running service", "GUI restores the exact original settings with a fresh revision"];
    }
}

internal sealed class ControllerTargetDialog : WorkspaceForm
{
    private readonly TextBox id = new(), path = new(), program = new(), routine = new(), methods = new() { Multiline = true, ScrollBars = ScrollBars.Vertical }, gateway = new(), route = new();
    private readonly CheckBox unrestricted = new() { Text = "All SDK methods and program/routine scopes", AutoSize = true }, cip = new() { Text = "Configure CIP reader route", AutoSize = true };
    private readonly NumericUpDown timeout = new() { Minimum = 100, Maximum = 30000, Value = 3000 };
    public ControllerTarget? Target { get; private set; }
    public ControllerTargetDialog(ControllerTarget? target)
    {
        Text = "Controller target and access policy"; Size = new(900, 760); MinimumSize = new(760, 660); Font = new("Segoe UI", 10);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new(12) }; table.ColumnStyles.Add(new(SizeType.Absolute, 210)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Add(string label, Control control, int height = 38) { var row = table.RowCount++; table.RowStyles.Add(new(SizeType.Absolute, height)); control.Dock = DockStyle.Fill; table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); table.Controls.Add(control, 1, row); }
        Add("Target ID", id); Add("SDK communications path", path); Add("Unrestricted target", unrestricted); Add("Restricted program (optional)", program); Add("Restricted routine (optional)", routine); Add("Allowed methods (one per line)", methods, 180);
        Add("CIP backend", cip); Add("CIP IPv4 gateway", gateway); Add("CIP route bytes", route); Add("CIP timeout (ms)", timeout);
        id.Text = target?.Id; path.Text = target?.CommunicationsPath; unrestricted.Checked = target?.Policy is null;
        program.Text = target?.Policy?.ProgramName; routine.Text = target?.Policy?.RoutineName; methods.Lines = target?.Policy?.AllowedMethods ?? [];
        cip.Checked = target?.CipRead is not null; gateway.Text = target?.CipRead?.Gateway; route.Text = target?.CipRead?.Route; timeout.Value = target?.CipRead?.TimeoutMs ?? 3000;
        void EnablePolicy() { program.Enabled = routine.Enabled = methods.Enabled = !unrestricted.Checked; gateway.Enabled = route.Enabled = timeout.Enabled = cip.Checked; }
        unrestricted.CheckedChanged += (_, _) => EnablePolicy(); cip.CheckedChanged += (_, _) => EnablePolicy(); EnablePolicy();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(8) };
        var all = new Button { Text = "Fill all method names", AutoSize = true }; all.Click += (_, _) => methods.Lines = SdkCatalog.Methods.Select(m => m.Name).Order().ToArray(); buttons.Controls.Add(all);
        var save = new Button { Text = "Use target", AutoSize = true }; save.Click += (_, _) =>
        {
            try
            {
                Target = new(id.Text.Trim(), path.Text.Trim(), unrestricted.Checked ? null : new(methods.Lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray(), Empty(program.Text), Empty(routine.Text)),
                    cip.Checked ? new(gateway.Text.Trim(), route.Text.Trim(), (int)timeout.Value) : null);
                if (Target.Id.Length == 0 || Target.CommunicationsPath.Length == 0) throw new ArgumentException("Target ID and SDK communications path are required.");
                SdkTargetPolicies.ValidateConfiguration(Target); if (Target.CipRead is not null) CipWatchPlanning.ValidateTarget(Target.CipRead);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Target settings"); }
        }; buttons.Controls.Add(save); var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true }; buttons.Controls.Add(cancel); CancelButton = cancel;
        Controls.Add(table); Controls.Add(buttons);
    }
    private static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
