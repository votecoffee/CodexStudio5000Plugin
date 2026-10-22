namespace Studio5000.Core;

public sealed record ServiceManagementActivity(string Version, int ActiveWorkspaces, int QueuedWorkspaces, int NativeJobs, int PollingWatches);

public sealed partial class ProjectService
{
    // In-memory counts only: never enumerate projects, plans, job files or controller targets.
    public ServiceManagementActivity ManagementActivity()
    {
        var workspace = gate.Status(); int polling;
        lock (watchLock) polling = watches.Values.Count(w => w.Task is { IsCompleted: false });
        return new(WorkspaceIdentity.Version, workspace.ActiveMutations, workspace.QueuedMutations, sdkJobs.Count, polling);
    }
}
