using System.Text.Json;
using Studio5000.Core;
using Studio5000.Extensibility;
using Xunit;

namespace Studio5000.Tests;

public sealed class ExtensionHostTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ExtensionTests", Guid.NewGuid().ToString("N"));
    private readonly ServiceOptions options;
    private readonly List<FakeWorker> workers = [];
    private readonly ExtensionHost host;
    private ExtensionOperation[] next = [Operation()];
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, ExtensionProtocol.Json);
    private static ExtensionOperation Operation(string effect = "read") => new("echo", "Echo development fixture", effect,
        J(new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" }, additionalProperties = false }));
    public ExtensionHostTests()
    {
        Directory.CreateDirectory(root);
        options = new() { StateDirectory = Path.Combine(root, "state"), ExtensionsDirectory = Path.Combine(root, "packages") };
        host = new(options, root, (_, _) => { var worker = new FakeWorker(next, workers.Count + 1); workers.Add(worker); return worker; });
    }
    private void Package(string version, bool badHash = false, int contract = 1, string file = "fixture.dll")
    {
        var dir = Path.Combine(options.ExtensionsDirectory, "demo", "versions", version); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fixture.dll"), "fixture " + version);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new ExtensionPackage("demo", version, contract,
            "fixture.dll", "Fixture", new() { [file] = badHash ? new string('0', 64) : ProjectService.Hash(File.ReadAllBytes(Path.Combine(dir, "fixture.dll"))) }), ExtensionProtocol.Json));
    }
    private static ExtensionCall Call(ExtensionSummary summary, object? arguments = null) => new("demo", "echo", summary.Generation, J(arguments ?? new { text = "hi" }));

    [Fact]
    public async Task ReloadPinsRunningJobAndPublishesNewGenerationAtomically()
    {
        Package("1"); var first = await host.ReloadAsync(new("demo", "1")); workers[0].Block = true;
        var job = await host.StartJobAsync(new("read", Call(first)));
        await workers[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Package("2"); var second = await host.ReloadAsync(new("demo", "2", first.Generation));
        Assert.NotEqual(first.Generation, second.Generation); Assert.False(workers[0].Disposed);
        var catalog = await host.CatalogAsync(); Assert.Contains(catalog.Extensions, x => x.Generation == first.Generation && x.State == "draining" && x.ActiveCalls == 1);
        Assert.Equal(2, (await host.InvokeAsync("read", Call(second))).GetProperty("worker").GetInt32());
        workers[0].Finish.TrySetResult();
        for (var i = 0; i < 200 && host.Job(job.Id).Status is "queued" or "running"; i++) await Task.Delay(10);
        Assert.Equal("succeeded", host.Job(job.Id).Status); Assert.Equal(1, host.Job(job.Id).Result!.Value.GetProperty("worker").GetInt32());
        Assert.True(workers[0].Disposed); Assert.Single((await host.CatalogAsync()).Extensions);
    }
    [Theory]
    [InlineData(true, 1, "fixture.dll")]
    [InlineData(false, 99, "fixture.dll")]
    [InlineData(false, 1, "../fixture.dll")]
    public async Task FailedPackageLeavesActiveGenerationAndPointerUnchanged(bool badHash, int contract, string file)
    {
        Package("1"); var first = await host.ReloadAsync(new("demo", "1")); Package("2", badHash, contract, file);
        await Assert.ThrowsAsync<OperationException>(() => host.ReloadAsync(new("demo", "2")));
        Assert.Equal(first.Generation, Assert.Single((await host.CatalogAsync()).Extensions).Generation);
        Assert.Equal("1", JsonSerializer.Deserialize<ExtensionPointer>(File.ReadAllText(Path.Combine(options.ExtensionsDirectory, "demo", "active.json")), ExtensionProtocol.Json)!.Version);
        Assert.Equal(1, (await host.InvokeAsync("read", Call(first))).GetProperty("worker").GetInt32());
    }
    [Fact]
    public async Task StaleGenerationAndWrongEffectAreRejectedBeforeDispatch()
    {
        Package("1"); next = [Operation("control")]; var first = await host.ReloadAsync(new("demo", "1"));
        Assert.Equal("effect_mismatch", (await Assert.ThrowsAsync<OperationException>(() => host.InvokeAsync("read", Call(first)))).Code);
        Assert.Equal(0, workers[0].Calls);
        Package("2"); var second = await host.ReloadAsync(new("demo", "2"));
        Assert.Equal("stale_generation", (await Assert.ThrowsAsync<OperationException>(() => host.InvokeAsync("control", Call(first)))).Code);
        Assert.Equal("stale_generation", (await Assert.ThrowsAsync<OperationException>(() => host.ReloadAsync(new("demo", "1", first.Generation)))).Code);
        Assert.Equal(second.Generation, Assert.Single((await host.CatalogAsync()).Extensions).Generation);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":7}")]
    [InlineData("{\"text\":\"hi\",\"unexpected\":1}")]
    public async Task InvalidArgumentsNeverReachWorker(string json)
    {
        Package("1"); var summary = await host.ReloadAsync(new("demo", "1"));
        Assert.Equal("invalid_arguments", (await Assert.ThrowsAsync<OperationException>(() => host.InvokeAsync("read", Call(summary) with { Arguments = JsonDocument.Parse(json).RootElement.Clone() }))).Code);
        Assert.Equal(0, workers[0].Calls);
    }
    [Fact]
    public async Task UnsupportedSchemaIsRejectedAndCandidateDisposed()
    {
        Package("1"); next = [Operation() with { InputSchema = J(new { type = "object", oneOf = new[] { new { type = "object" } } }) }];
        await Assert.ThrowsAsync<OperationException>(() => host.ReloadAsync(new("demo", "1")));
        Assert.True(workers[0].Disposed); Assert.Empty((await host.CatalogAsync()).Extensions);
    }
    [Fact]
    public async Task PreviouslyLoadedVersionCannotBeReplacedWithDifferentBytes()
    {
        Package("1"); await host.ReloadAsync(new("demo", "1"));
        var path = Path.Combine(options.ExtensionsDirectory, "demo", "versions", "1", "manifest.json");
        File.AppendAllText(path, " ");
        Assert.Contains("publish a new version", (await Assert.ThrowsAsync<OperationException>(() => host.ReloadAsync(new("demo", "1")))).Message);
        Assert.Single(workers);
    }
    [Fact]
    public async Task CancellationIsIndeterminateAndArgumentPayloadIsNotPersisted()
    {
        Package("1"); var summary = await host.ReloadAsync(new("demo", "1")); workers[0].Block = true;
        var job = await host.StartJobAsync(new("read", Call(summary, new { text = "private payload marker" })));
        await workers[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); host.CancelJob(job.Id);
        for (var i = 0; i < 200 && host.Job(job.Id).Status is "queued" or "running"; i++) await Task.Delay(10);
        Assert.Equal("indeterminate", host.Job(job.Id).Status);
        Assert.DoesNotContain("private payload marker", File.ReadAllText(Path.Combine(options.StateDirectory, "extensions", "job-" + job.Id + ".json")));
        Assert.Equal(0, Assert.Single((await host.CatalogAsync()).Extensions).ActiveCalls);
    }
    [Fact]
    public async Task StartupRestoresActivePackageAndMarksInterruptedJobsIndeterminate()
    {
        Package("1"); await host.ReloadAsync(new("demo", "1"));
        var job = new ExtensionJob(Guid.NewGuid().ToString("N"), "demo", "echo", "old", "apply", "running", DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(options.StateDirectory, "extensions", "job-" + job.Id + ".json"), JsonSerializer.Serialize(job, ExtensionProtocol.Json));
        using var restarted = new ExtensionHost(options, root, (_, _) => new FakeWorker(next, 3));
        Assert.Equal("1", Assert.Single((await restarted.CatalogAsync()).Extensions).Version);
        Assert.Equal("indeterminate", restarted.Job(job.Id).Status);
    }
    public void Dispose() { host.Dispose(); Directory.Delete(root, true); }
    private sealed class FakeWorker(ExtensionOperation[] operations, int number) : IExtensionWorker
    {
        public bool Block, Disposed;
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct) => Task.FromResult(operations);
        public async Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct)
        { Calls++; Started.TrySetResult(); if (Block) await Finish.Task.WaitAsync(ct); return J(new { worker = number }); }
        public void Dispose() { Disposed = true; Finish.TrySetResult(); }
    }
}
