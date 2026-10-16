using Studio5000.Client;
using Studio5000.Desktop;

ApplicationConfiguration.Initialize();
try
{
    var root = ServiceClient.FindRoot();
    using var client = new ServiceClient(root);
    using var form = new MainForm(client);
    if (args.Length == 2 && args[0] == "--self-test")
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.ShowInTaskbar = false;
        form.Show(); // create the real control handles off screen
        Application.DoEvents();
        form.LoadForTest(Path.Combine(root, "examples", "Demo.L5X"));
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
    if (args.Contains("--self-test")) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    else MessageBox.Show(ex.Message, "Studio 5000 service", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
