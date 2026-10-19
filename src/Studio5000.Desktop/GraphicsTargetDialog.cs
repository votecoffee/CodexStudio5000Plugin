namespace Studio5000.Desktop;

internal sealed class GraphicsTargetDialog : WorkspaceForm
{
    private readonly TextBox application = new() { Dock = DockStyle.Fill };
    private readonly TextBox display = new() { Dock = DockStyle.Fill };
    private readonly TextBox language = new() { Dock = DockStyle.Fill, PlaceholderText = "Confirm the current Studio language, e.g. en-US" };
    public string ApplicationName => application.Text.Trim();
    public string DisplayName => display.Text.Trim();
    public string Language => language.Text.Trim();

    public GraphicsTargetDialog(string displayName)
    {
        Text = "FactoryTalk native import target"; Size = new Size(610, 280); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        Font = new Font("Segoe UI", 10); display.Text = displayName;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = "Application / server", AutoSize = true }, 0, 0); table.Controls.Add(application, 1, 0);
        table.Controls.Add(new Label { Text = "Target display", AutoSize = true }, 0, 1); table.Controls.Add(display, 1, 1);
        table.Controls.Add(new Label { Text = "Current language", AutoSize = true }, 0, 2); table.Controls.Add(language, 1, 2);
        var note = new Label { Text = "Confirm these values in Studio. Preparation creates files for an update import; the native wizard performs the application change.", Dock = DockStyle.Fill, AutoSize = true };
        table.Controls.Add(note, 0, 3); table.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var prepare = new Button { Text = "Prepare", AutoSize = true };
        prepare.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ApplicationName) || string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(Language))
            { MessageBox.Show(this, "Enter the exact application, display and current language."); return; }
            DialogResult = DialogResult.OK; Close();
        };
        buttons.Controls.Add(prepare); buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true });
        table.Controls.Add(buttons, 0, 4); table.SetColumnSpan(buttons, 2); Controls.Add(table); AcceptButton = prepare;
    }
}
