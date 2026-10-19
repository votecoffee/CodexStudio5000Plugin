using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class WorkspaceConcurrencyTests
{
    [Fact]
    public async Task DisjointResourcesOverlapWhileSameResourceAndGlobalRequestsWaitFairly()
    {
        using var gate = new WorkspaceCoordinator(3, 16);
        using var a = await gate.EnterAsync(["a"]); using var b = await gate.EnterAsync(["b"]);
        var blocked = gate.EnterAsync(["a"]); var global = gate.WaitAsync(); var later = gate.EnterAsync(["c"]);
        Assert.False(blocked.IsCompleted); Assert.False(global.IsCompleted); Assert.False(later.IsCompleted);
        Assert.Equal(2, gate.Status().ActiveMutations); a.Dispose(); using var next = await blocked.WaitAsync(TimeSpan.FromSeconds(3));
        b.Dispose(); Assert.False(global.IsCompleted); next.Dispose(); await global.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(gate.Status().ExclusiveActive); Assert.False(later.IsCompleted); gate.Release(); using var c = await later.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(gate.Status().ExclusiveActive);
    }
    [Fact]
    public async Task CancelledQueuedRequestsLeaveNoLocksOrCapacityBehind()
    {
        using var gate = new WorkspaceCoordinator(1, 1); using var held = await gate.EnterAsync(["a"]); using var cancel = new CancellationTokenSource();
        var blocked = gate.EnterAsync(["b"], cancel.Token);
        await Assert.ThrowsAsync<OperationException>(() => gate.EnterAsync(["c"])); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.Equal(0, gate.Status().QueuedMutations); var next = gate.EnterAsync(["b"]); held.Dispose(); using var result = await next.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, gate.Status().ActiveMutations);
    }
    [Fact]
    public async Task OverlappingResourceSetsAreAcquiredTogetherWithoutDeadlock()
    {
        using var gate = new WorkspaceCoordinator(); using var a = await gate.EnterAsync(["a", "b"]);
        var blocked = gate.EnterAsync(["b", "c"]); using var independent = await gate.EnterAsync(["d"]);
        var conflicting = gate.EnterAsync(["c"]); Assert.False(conflicting.IsCompleted);
        a.Dispose(); using var bc = await blocked.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(conflicting.IsCompleted); bc.Dispose(); using var c = await conflicting.WaitAsync(TimeSpan.FromSeconds(3));
    }
    [Fact]
    public async Task DisposalUnblocksQueuedRequestsAndActiveLeasesCanStillBeReleased()
    {
        var gate = new WorkspaceCoordinator(); using var held = await gate.EnterAsync(["a"]); var pending = gate.EnterAsync(["a"]); gate.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending); held.Dispose(); Assert.Equal(0, gate.Status().ActiveMutations);
    }
}
