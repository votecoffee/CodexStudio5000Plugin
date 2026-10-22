namespace Studio5000.Core;

public sealed record ServiceManagementActivity(string Version, int ActiveWorkspaces, int QueuedWorkspaces, int NativeJobs, int PollingWatches);

public sealed partial class ProjectService
{
    private bool shutdownRequested;
    public bool TryFenceShutdown()
    {
        lock (watchLock)
        {
            if (sdkJobs.Count != 0 || watches.Values.Any(w => w.Task is { IsCompleted: false }) || !gate.TryFenceShutdown()) return false;
            shutdownRequested = true; return true;
        }
    }
    public void ReleaseShutdownFence()
    { lock (watchLock) { shutdownRequested = false; gate.ReleaseShutdownFence(); } }
    // In-memory counts only: never enumerate projects, plans, job files or controller targets.
    public ServiceManagementActivity ManagementActivity()
    {
        var workspace = gate.Status(); int polling;
        lock (watchLock) polling = watches.Values.Count(w => w.Task is { IsCompleted: false });
        return new(WorkspaceIdentity.Version, workspace.ActiveMutations, workspace.QueuedMutations, sdkJobs.Count, polling);
    }
}
