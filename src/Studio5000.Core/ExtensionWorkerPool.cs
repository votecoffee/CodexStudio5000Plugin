using System.Text.Json;
using Studio5000.Extensibility;

namespace Studio5000.Core;

// Explicit manifest opt-in only: extension instances must not depend on private in-process state.
public sealed class ExtensionWorkerPool : IExtensionWorker
{
    private readonly Func<IExtensionWorker> factory;
    private readonly SemaphoreSlim slots, describeGate = new(1);
    private readonly object sync = new();
    private readonly Queue<IExtensionWorker> idle = new();
    private readonly HashSet<IExtensionWorker> workers = [];
    private readonly CancellationTokenSource shutdown = new();
    private ExtensionOperation[]? operations;
    private string? catalog;
    private bool disposed;
    public ExtensionWorkerPool(int maximumWorkers, Func<IExtensionWorker> factory)
    {
        if (maximumWorkers is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximumWorkers));
        this.factory = factory; slots = new(maximumWorkers, maximumWorkers);
        var worker = factory(); workers.Add(worker); idle.Enqueue(worker);
    }
    public async Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct)
    {
        await describeGate.WaitAsync(ct);
        try
        {
            if (operations is not null) return operations.ToArray();
            var worker = idle.Peek(); var result = await worker.DescribeAsync(ct);
            catalog = JsonSerializer.Serialize(result, ExtensionProtocol.Json); operations = result; return result.ToArray();
        }
        finally { describeGate.Release(); }
    }
    public async Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        await DescribeAsync(ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
        await slots.WaitAsync(linked.Token); IExtensionWorker? worker = null; var reusable = true;
        try
        {
            var fresh = false;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (idle.Count != 0) worker = idle.Dequeue();
                else { worker = factory(); workers.Add(worker); fresh = true; }
            }
            if (fresh && JsonSerializer.Serialize(await worker.DescribeAsync(linked.Token), ExtensionProtocol.Json) != catalog)
            { reusable = false; throw new OperationException("invalid_extension", "Parallel worker catalog differs from the validated generation."); }
            return await worker.InvokeAsync(operation, arguments, linked.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException || ex is OperationException { Code: "extension_indeterminate" })
        { reusable = false; throw; }
        finally
        {
            if (worker is not null)
            {
                lock (sync)
                {
                    if (disposed || !reusable) { workers.Remove(worker); reusable = false; } else idle.Enqueue(worker);
                }
                if (!reusable) worker.Dispose();
            }
            slots.Release();
        }
    }
    public void Dispose()
    {
        IExtensionWorker[] owned;
        lock (sync) { if (disposed) return; disposed = true; owned = workers.ToArray(); workers.Clear(); idle.Clear(); }
        shutdown.Cancel(); foreach (var worker in owned) worker.Dispose();
    }
}
