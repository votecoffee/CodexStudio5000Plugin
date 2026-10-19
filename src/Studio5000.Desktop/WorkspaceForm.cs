namespace Studio5000.Desktop;

public class WorkspaceForm : Form
{
    private readonly Icon applicationIcon;

    public WorkspaceForm()
    {
        using var stream = typeof(WorkspaceForm).Assembly.GetManifestResourceStream("Studio5000.Desktop.Application.ico")
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using var source = new Icon(stream);
        applicationIcon = (Icon)source.Clone();
        Icon = applicationIcon;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) applicationIcon.Dispose();
    }
}
