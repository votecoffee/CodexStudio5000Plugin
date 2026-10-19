using System.Text.Json;
using Studio5000.Core;
using Studio5000.Extensibility;
using Xunit;

namespace Studio5000.Tests;

public sealed class ExtensionWorkerPoolTests
{
    private sealed class Worker(Func<CancellationToken, Task<JsonElement>> call, string name = "echo") : IExtensionWorker
    {
        public bool Disposed;
        public Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct) => Task.FromResult(new[] { new ExtensionOperation(name, "fixture", "read", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })) });
        public Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct) => call(ct);
        public void Dispose() => Disposed = true;
    }
    [Fact]
    public async Task OptedInGenerationRunsIndependentCallsInSeparateWorkersWithoutCrossingResponses()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var active = 0; var created = 0;
        using var pool = new ExtensionWorkerPool(2, () =>
        {
            var id = Interlocked.Increment(ref created); return new Worker(async ct => { if (Interlocked.Increment(ref active) == 2) started.TrySetResult(); await release.Task.WaitAsync(ct); return JsonSerializer.SerializeToElement(id); });
        });
        await pool.DescribeAsync(default); var first = pool.InvokeAsync("echo", default, default); var second = pool.InvokeAsync("echo", default, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(2, created); release.SetResult(); var replies = await Task.WhenAll(first, second); Assert.Equal([1, 2], replies.Select(r => r.GetInt32()).Order().ToArray());
    }
    [Fact]
    public async Task CancellingAQueuedCallDoesNotInterruptAnotherInvocation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var owned = new List<Worker>();
        using var pool = new ExtensionWorkerPool(1, () => { var w = new Worker(async ct => { await release.Task.WaitAsync(ct); return JsonSerializer.SerializeToElement("done"); }); owned.Add(w); return w; });
        await pool.DescribeAsync(default); var first = pool.InvokeAsync("echo", default, default); using var cancel = new CancellationTokenSource(); var second = pool.InvokeAsync("echo", default, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); Assert.False(owned[0].Disposed); release.SetResult(); Assert.Equal("done", (await first).GetString());
    }
    [Fact]
    public async Task AWorkerWithADifferentCatalogCannotRunInsideTheSameGeneration()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        using var pool = new ExtensionWorkerPool(2, () => new Worker(async ct => { await release.Task.WaitAsync(ct); return default; }, Interlocked.Increment(ref count) == 1 ? "echo" : "different"));
        await pool.DescribeAsync(default); var first = pool.InvokeAsync("echo", default, default);
        await Assert.ThrowsAsync<OperationException>(() => pool.InvokeAsync("echo", default, default)); release.SetResult(); await first;
    }
    [Fact]
    public void LegacyManifestsKeepSingleWorkerSemantics()
    {
        var manifest = JsonSerializer.Deserialize<ExtensionPackage>("{\"id\":\"legacy\",\"version\":\"1\",\"contractVersion\":1,\"entryAssembly\":\"a.dll\",\"entryType\":\"Entry\",\"files\":{}}", ExtensionProtocol.Json)!;
        Assert.Equal(1, manifest.ParallelWorkers);
    }
}
