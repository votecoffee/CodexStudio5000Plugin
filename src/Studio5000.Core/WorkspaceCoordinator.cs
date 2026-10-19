namespace Studio5000.Core;

public sealed record WorkspaceConcurrencyStatus(int MaximumConcurrentMutations, int MaximumQueuedMutations, int ActiveMutations,
    int QueuedMutations, bool ExclusiveActive, string[] ActiveResources);

// Disjoint resource sets may overlap. Legacy compound operations retain an exclusive barrier.
// A blocked earlier request prevents later conflicting requests from overtaking it.
public sealed class WorkspaceCoordinator(int maximumActive = 8, int maximumQueued = 256) : IDisposable
{
    private sealed class Pending(string[]? keys, CancellationToken ct)
    {
        internal readonly string[]? Keys = keys;
        internal readonly CancellationToken Token = ct;
        internal readonly TaskCompletionSource<IDisposable> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Pending>? Node;
        internal CancellationTokenRegistration Registration;
    }
    private sealed class Lease(WorkspaceCoordinator owner, long id) : IDisposable
    {
        private WorkspaceCoordinator? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseLease(id);
    }
    private readonly object sync = new();
    private readonly LinkedList<Pending> waiting = new();
    private readonly Dictionary<long, string[]?> active = [];
    private readonly HashSet<string> resources = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? legacy;
    private bool exclusive, disposed;
    private long sequence;
    internal Action? BeforeExclusive { get; set; }
    public WorkspaceConcurrencyStatus Status()
    { lock (sync) return new(maximumActive, maximumQueued, active.Count, waiting.Count, exclusive, resources.Order(StringComparer.Ordinal).ToArray()); }
    public Task<IDisposable> EnterAsync(string[] keys, CancellationToken ct = default)
    {
        if (keys is null || keys.Length is < 1 or > 16 || keys.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 1024)) throw new ArgumentException("Supply 1..16 bounded resource keys.");
        return Enqueue(keys.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), ct);
    }
    private Task<IDisposable> Enqueue(string[]? keys, CancellationToken ct)
    {
        if (maximumActive is < 1 or > 32 || maximumQueued is < 1 or > 4096) throw new ArgumentException("Concurrency limits must be active 1..32 and queued 1..4096.");
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); ct.ThrowIfCancellationRequested();
            if (waiting.Count >= maximumQueued) throw new OperationException("request_queue_full", "Mutation queue is full; retry later after inspecting the operation's result.");
            var request = new Pending(keys, ct); request.Node = waiting.AddLast(request);
            request.Registration = ct.UnsafeRegister(_ => Cancel(request), null);
            if (request.Node is null) request.Registration.Unregister();
            Drain(); return request.Completion.Task;
        }
    }
    private void Cancel(Pending request)
    {
        lock (sync)
        {
            if (request.Node is null) return;
            waiting.Remove(request.Node); request.Node = null; request.Registration.Unregister(); request.Completion.TrySetCanceled(request.Token); Drain();
        }
    }
    private void Drain()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var node = waiting.First; node is not null;)
        {
            var next = node.Next; var request = node.Value;
            if (request.Keys is null)
            {
                if (active.Count == 0) Grant(request);
                break;
            }
            if (!exclusive && active.Count < maximumActive && !request.Keys.Any(k => resources.Contains(k) || blocked.Contains(k))) Grant(request);
            else blocked.UnionWith(request.Keys);
            node = next;
        }
    }
    private void Grant(Pending request)
    {
        waiting.Remove(request.Node!); request.Node = null; request.Registration.Unregister(); var id = ++sequence;
        active.Add(id, request.Keys);
        if (request.Keys is null) exclusive = true; else resources.UnionWith(request.Keys);
        request.Completion.SetResult(new Lease(this, id));
    }
    private void ReleaseLease(long id)
    {
        lock (sync)
        {
            if (!active.Remove(id, out var keys)) return;
            if (keys is null) exclusive = false; else resources.ExceptWith(keys);
            if (!disposed) Drain();
        }
    }
    // Compatibility with unchanged global compound-operation call sites.
    public async Task WaitAsync(CancellationToken ct = default)
    {
        var lease = await Enqueue(null, ct); legacy = lease;
        try { BeforeExclusive?.Invoke(); } catch { Release(); throw; }
    }
    public void Release() => Interlocked.Exchange(ref legacy, null)?.Dispose();
    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            foreach (var request in waiting) { request.Node = null; request.Registration.Unregister(); request.Completion.TrySetException(new ObjectDisposedException(nameof(WorkspaceCoordinator))); }
            waiting.Clear();
        }
    }
}
