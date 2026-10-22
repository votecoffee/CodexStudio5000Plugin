using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ManagementActivityTests
{
    [Fact]
    public void Counts_do_not_open_project_files_or_initialize_extension_workers()
    {
        var root = Path.Combine(Path.GetTempPath(), "RockwellManagement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] };
            using var service = new ProjectService(options);
            using var extensions = new ExtensionHost(options, root, (_, _) => throw new Exception("A metadata read started a worker."));
            var invalid = Path.Combine(options.StateDirectory, "projects", "unreadable.json"); File.WriteAllText(invalid, "invalid generated project metadata");
            using var held = new FileStream(invalid, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var status = service.ManagementActivity();
            Assert.Equal(WorkspaceIdentity.Version, status.Version); Assert.Equal(0, status.NativeJobs); Assert.Equal(0, status.PollingWatches);
            Assert.Equal(0, status.ActiveWorkspaces); Assert.Equal(0, status.QueuedWorkspaces); Assert.Equal(0, extensions.ManagementActivity());
        }
        finally { Directory.Delete(root, true); }
    }
}
