using Studio5000.Client;
using Studio5000.Desktop;

ApplicationConfiguration.Initialize();
try
{
    var root = ServiceClient.FindRoot();
    using var client = new ServiceClient(root);
    if (args.Length == 2 && args[0] == "--self-test-extensions")
    {
        using var extensions = new ExtensionsForm(client);
        extensions.StartPosition = FormStartPosition.Manual; extensions.Location = new(-30000, -30000); extensions.ShowInTaskbar = false;
        extensions.LoadForTest(); extensions.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(extensions.Width, extensions.Height);
        extensions.DrawToBitmap(bitmap, new Rectangle(Point.Empty, extensions.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        extensions.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-viewdesigner")
    {
        using var view = new ViewDesignerForm(client);
        view.StartPosition = FormStartPosition.Manual; view.Location = new(-30000, -30000); view.ShowInTaskbar = false;
        view.LoadForTest(); view.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        view.Close(); return;
    }
    if (args.Length == 2 && args[0] is "--self-test-watches" or "--self-test-watches-values")
    {
        using var watches = new WatchForm(client, null);
        watches.StartPosition = FormStartPosition.Manual; watches.Location = new(-30000, -30000); watches.ShowInTaskbar = false;
        var settings = new Studio5000.Core.WatchListRequest("UDT process values", [new("record", "Record", "WatchProgram")], new string('0', 64), SourcePath: "Example.ACD", Mode: "Offline");
        var sample = new Studio5000.Core.WatchSnapshot(3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Offline",
            [new("record", "Record", "WatchProgram", "WatchRecord", System.Text.Json.Nodes.JsonNode.Parse("{\"Count\":7,\"Position\":1.5,\"Inner\":{\"Count\":8},\"Samples\":[10,20,30]}"), "good")]);
        watches.LoadForTest(new(new("00000000000000000000000000000000", 0, DateTimeOffset.UtcNow, settings), "stopped", sample, true, null, []), showValues: args[0].EndsWith("values", StringComparison.Ordinal));
        watches.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(watches.Width, watches.Height);
        watches.DrawToBitmap(bitmap, new Rectangle(Point.Empty, watches.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        watches.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-online-edit")
    {
        using var online = new OnlineEditDialog(client);
        online.StartPosition = FormStartPosition.Manual; online.Location = new(-30000, -30000); online.ShowInTaskbar = false;
        online.LoadForTest(); online.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(online.Width, online.Height);
        online.DrawToBitmap(bitmap, new Rectangle(Point.Empty, online.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        online.Close(); return;
    }
    if (args.Length == 2 && args[0] == "--self-test-sdk")
    {
        using var sdk = new SdkForm(client, null);
        sdk.StartPosition = FormStartPosition.Manual; sdk.Location = new(-30000, -30000); sdk.ShowInTaskbar = false;
        sdk.LoadForTest();
        sdk.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(sdk.Width, sdk.Height);
        sdk.DrawToBitmap(bitmap, new Rectangle(Point.Empty, sdk.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        sdk.Close(); return;
    }
    using var form = new MainForm(client);
    if (args.Length == 2 && args[0] == "--self-test")
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.ShowInTaskbar = false;
        form.Show(); // create the real control handles off screen
        Application.DoEvents();
        form.LoadForTest(Path.Combine(root, "examples", Environment.GetEnvironmentVariable("STUDIO5000_GUI_FIXTURE") ?? "Demo.L5X"));
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
        return;
    }
    form.Shown += async (_, _) => await form.InitializeAsync(args.Length == 2 && args[0] == "--project" ? args[1] : null);
    Application.Run(form);
}
catch (Exception ex)
{
    if (args.Any(a => a.StartsWith("--self-test", StringComparison.Ordinal))) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    else MessageBox.Show(ex.Message, "Studio 5000 service", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
