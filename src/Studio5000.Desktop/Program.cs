using Studio5000.Client;
using Studio5000.Desktop;

ApplicationConfiguration.Initialize();
try
{
    var root = ServiceClient.FindRoot();
    using var client = new ServiceClient(root);
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
    if (args.Contains("--self-test") || args.Contains("--self-test-sdk") || args.Contains("--self-test-online-edit")) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    else MessageBox.Show(ex.Message, "Studio 5000 service", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
