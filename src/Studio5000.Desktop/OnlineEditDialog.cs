using Studio5000.Client;
using Studio5000.Core;
using System.Text.Json;

namespace Studio5000.Desktop;

public sealed class OnlineEditDialog : Form
{
    private readonly ServiceClient client;
    private readonly TextBox source = new() { Dock = DockStyle.Fill };
    private readonly TextBox import = new() { Dock = DockStyle.Fill };
    private readonly TextBox program = new() { Text = "MainProgram", Dock = DockStyle.Fill };
    private readonly TextBox routine = new() { Text = "MainRoutine", Dock = DockStyle.Fill };
    private readonly ComboBox target = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Id" };
    private readonly ComboBox workflow = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox kind = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown position = new() { Maximum = uint.MaxValue, Dock = DockStyle.Fill };
    private readonly NumericUpDown count = new() { Maximum = uint.MaxValue, Value = 1, Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Button prepare = new() { Text = "Prepare plan", AutoSize = true };
    private bool testLoaded;
    public OnlineEditPlan? Plan { get; private set; }

    public OnlineEditDialog(ServiceClient client)
    {
        this.client = client; Text = "Prepare online edit — no connection";
        ClientSize = new(830, 550); MinimumSize = new(750, 590); StartPosition = FormStartPosition.CenterParent;
        Font = new("Segoe UI", 10);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 12, Padding = new(14) };
        table.ColumnStyles.Add(new(SizeType.Absolute, 160)); table.ColumnStyles.Add(new(SizeType.Percent, 100)); table.ColumnStyles.Add(new(SizeType.Absolute, 92));
        void Row(int row, string label, Control control, string? filter = null)
        {
            table.RowStyles.Add(new(SizeType.Absolute, 36));
            table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            table.Controls.Add(control, 1, row);
            if (filter is not null)
            {
                var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
                browse.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = filter }; if (dialog.ShowDialog(this) == DialogResult.OK) control.Text = dialog.FileName; };
                table.Controls.Add(browse, 2, row);
            }
        }
        Row(0, "Configured target", target); Row(1, "Saved matching ACD", source, "Logix project|*.acd");
        Row(2, "Edited component XML", import, "Logix export|*.l5x;*.xml"); Row(3, "Program", program); Row(4, "Routine", routine);
        Row(5, "Import kind", kind); Row(6, "Workflow", workflow); Row(7, "Insert rung position", position); Row(8, "Replace rung count", count);
        kind.Items.AddRange(["rungs", "routine"]); kind.SelectedIndex = 0;
        workflow.Items.AddRange(["accept_for_test", "leave_pending", "finalize_during_import"]); workflow.SelectedIndex = 0;
        kind.SelectedIndexChanged += (_, _) => { position.Enabled = count.Enabled = kind.Text == "rungs"; if (kind.Text == "routine") { position.Value = 0; count.Value = 0; } };
        var note = new Label { Text = "Accept for test stages an SDK request. Test/Untest/Assemble require Studio UI; controller Test mode is different. Finalize during import skips the test interval. Preparing does not connect or enable a target.", Dock = DockStyle.Fill };
        table.RowStyles.Add(new(SizeType.Absolute, 77)); table.Controls.Add(note, 0, 9); table.SetColumnSpan(note, 3);
        table.RowStyles.Add(new(SizeType.Percent, 100)); table.Controls.Add(status, 0, 10); table.SetColumnSpan(status, 3);
        table.RowStyles.Add(new(SizeType.Absolute, 40));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true }; buttons.Controls.Add(cancel); buttons.Controls.Add(prepare);
        table.Controls.Add(buttons, 0, 11); table.SetColumnSpan(buttons, 3); Controls.Add(table); CancelButton = cancel;
        Shown += async (_, _) =>
        {
            if (testLoaded) return;
            try
            {
                var caps = await client.GetAsync<JsonElement>("/api/sdk/capabilities");
                BindTargets(caps);
            }
            catch (Exception ex) { status.Text = ex.Message; prepare.Enabled = false; }
        };
        prepare.Click += async (_, _) =>
        {
            prepare.Enabled = false; UseWaitCursor = true;
            try
            {
                var sourceInfo = await client.PostAsync<JsonElement>("/api/sdk/source", new RegisterRequest(source.Text));
                var importInfo = await client.PostAsync<JsonElement>("/api/sdk/source", new RegisterRequest(import.Text));
                Plan = await client.PostAsync<OnlineEditPlan>("/api/sdk/online-edits/plan", new OnlineEditPlanRequest(
                    ((ControllerTarget)target.SelectedItem!).Id, program.Text, routine.Text,
                    new(import.Text, importInfo.GetProperty("sha256").GetString()!), sourceInfo.GetProperty("sha256").GetString()!,
                    SourcePath: source.Text, Workflow: workflow.Text, ImportKind: kind.Text, InsertPosition: (uint)position.Value, ReplaceCount: (uint)count.Value));
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { prepare.Enabled = true; UseWaitCursor = false; }
        };
    }
    private void BindTargets(JsonElement caps)
    {
        target.DataSource = caps.GetProperty("controllerTargets").Deserialize<ControllerTarget[]>(ProjectService.Json)!;
        prepare.Enabled = target.Items.Count > 0;
        if (!prepare.Enabled) status.Text = "No target configured. Add an explicitly chosen ControllerTarget in service.local.json and restart; leave controller operations disabled while preparing.";
    }
    public void LoadForTest()
    {
        BindTargets(Task.Run(() => client.GetAsync<JsonElement>("/api/sdk/capabilities")).GetAwaiter().GetResult());
        source.Text = @"C:\Projects\AuthorizedMachine.acd"; import.Text = @"C:\Projects\EditedRungs.l5x";
        testLoaded = true;
    }
}
