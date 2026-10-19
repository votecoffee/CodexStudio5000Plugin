using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Studio5000.Extensibility;

namespace Studio5000.Core;

public interface IExtensionWorker : IDisposable
{
    Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct);
    Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct);
}

// Owns version selection and leases. Domain state stays in the existing shared ProjectService.
public sealed class ExtensionHost : IDisposable
{
    private sealed class Generation(ExtensionSummary summary, IExtensionWorker worker)
    {
        public ExtensionSummary Summary = summary;
        public readonly IExtensionWorker Worker = worker;
        public int Leases;
        public bool Retired;
    }
    private readonly ServiceOptions options;
    private readonly string root, workspace, state;
    private readonly Func<string, string, IExtensionWorker> createWorker;
    private readonly object sync = new();
    private readonly SemaphoreSlim initialize = new(1), reload = new(1);
    private readonly Dictionary<string, Generation> active = new(StringComparer.Ordinal);
    private readonly List<Generation> retired = [];
    private readonly List<string> errors = [];
    private readonly Dictionary<string, (ExtensionJob Job, CancellationTokenSource Cancel)> jobs = new();
    private bool initialized, disposed;

    public ExtensionHost(ServiceOptions options, string workspaceRoot, Func<string, string, IExtensionWorker>? factory = null)
    {
        this.options = options; workspace = Path.GetFullPath(workspaceRoot);
        root = Path.GetFullPath(options.ExtensionsDirectory, workspace);
        state = Path.Combine(Path.GetFullPath(options.StateDirectory, workspace), "extensions");
        Directory.CreateDirectory(state); ProjectService.RejectLinks(state);
        createWorker = factory ?? ((assembly, type) => new ProcessExtensionWorker(Path.GetFullPath(options.ExtensionWorkerPath, workspace), assembly, type, workspace));
        foreach (var file in Directory.GetFiles(state, "job-*.json"))
        {
            var job = Read<ExtensionJob>(file);
            if (job.Status is "queued" or "running") Save(file, job with { Status = "indeterminate", Error = "Service ended before completion. Inspect effects before retrying." });
        }
    }
    public async Task<ExtensionCatalog> CatalogAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        lock (sync) return new(ExtensionProtocol.Version, active.Values.Concat(retired).Select(Summary).OrderBy(x => x.Id).ToArray(), errors.ToArray());
    }
    public async Task<ExtensionOperation> DescribeAsync(string id, string operation, string generation, CancellationToken ct = default)
    { await InitializeAsync(ct); lock (sync) return Find(id, operation, generation).Operation; }
    public async Task<ExtensionSummary> ReloadAsync(ExtensionReloadRequest request, CancellationToken ct = default)
    { await InitializeAsync(ct); return await ReloadCoreAsync(request, ct); }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await initialize.WaitAsync(ct);
        try
        {
            if (initialized) return;
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Directory.Exists(root))
            {
                ProjectService.RejectLinks(root);
                foreach (var directory in Directory.GetDirectories(root).Order())
                {
                    var pointer = Path.Combine(directory, "active.json"); if (!File.Exists(pointer)) continue;
                    try { await ReloadCoreAsync(new(Path.GetFileName(directory)), ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { lock (sync) errors.Add(Path.GetFileName(directory) + ": " + ex.Message); }
                }
            }
            initialized = true;
        }
        finally { initialize.Release(); }
    }
    private async Task<ExtensionSummary> ReloadCoreAsync(ExtensionReloadRequest request, CancellationToken ct)
    {
        var id = Name(request.ExtensionId); await reload.WaitAsync(ct);
        IExtensionWorker? worker = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync)
            {
                if (active.Count + retired.Count >= 16) throw Error("extension_busy", "Too many active/draining generations; wait for jobs to finish.");
                if (request.ExpectedGeneration is not null && (!active.TryGetValue(id, out var current) || current.Summary.Generation != request.ExpectedGeneration))
                    throw Error("stale_generation", "Extension generation changed; refresh the catalog.");
            }
            var version = Name(request.Version ?? Read<ExtensionPointer>(SafePath(root, id, "active.json")).Version);
            var packageDirectory = SafePath(root, id, "versions", version);
            var manifestPath = SafePath(packageDirectory, "manifest.json");
            var bytes = File.ReadAllBytes(manifestPath);
            if (bytes.Length > 256 * 1024) throw Error("invalid_extension", "Manifest exceeds 256 KiB.");
            var package = JsonSerializer.Deserialize<ExtensionPackage>(bytes, ExtensionProtocol.Json) ?? throw Error("invalid_extension", "Missing extension manifest.");
            var hash = ProjectService.Hash(bytes);
            if (package.Id != id || package.Version != version || package.ContractVersion != ExtensionProtocol.Version ||
                string.IsNullOrWhiteSpace(package.EntryType) || package.Files is null || package.Files.Count is < 1 or > 256 || package.ParallelWorkers is < 1 or > 4)
                throw Error("invalid_extension", "Manifest identity/contract/files are invalid.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            var snapshot = Path.Combine(state, "cache", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(snapshot); ProjectService.RejectLinks(snapshot);
            foreach (var file in package.Files)
            {
                var parts = file.Key.Split('/'); foreach (var part in parts) Name(part);
                if (!seen.Add(file.Key) || file.Key.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) throw Error("invalid_extension", "Ambiguous package file.");
                var input = SafePath(packageDirectory, parts);
                if (new FileInfo(input).Length > 64 * 1024 * 1024) throw Error("invalid_extension", "Package file exceeds 64 MiB.");
                var content = File.ReadAllBytes(input); total += content.Length;
                if (total > 128 * 1024 * 1024 || !ProjectService.Hash(content).Equals(file.Value, StringComparison.OrdinalIgnoreCase)) throw Error("invalid_extension", "Package size/hash validation failed: " + file.Key);
                var output = SafePath(snapshot, parts); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllBytes(output, content);
            }
            if (!seen.Contains(package.EntryAssembly) || !package.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw Error("invalid_extension", "Entry assembly must be a manifested DLL.");
            var ledger = Path.Combine(state, id + "-" + version + ".sha256");
            if (File.Exists(ledger) && File.ReadAllText(ledger) != hash) throw Error("invalid_extension", "Version was previously loaded with different bytes; publish a new version.");
            File.WriteAllBytes(Path.Combine(snapshot, "manifest.json"), bytes);
            var assemblyPath = SafePath(snapshot, package.EntryAssembly.Split('/'));
            worker = package.ParallelWorkers == 1 ? createWorker(assemblyPath, package.EntryType) : new ExtensionWorkerPool(package.ParallelWorkers, () => createWorker(assemblyPath, package.EntryType));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var operations = await worker.DescribeAsync(timeout.Token);
            if (operations is null || operations.Length is < 1 or > 128 || operations.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count() != operations.Length)
                throw Error("invalid_extension", "Supply 1–128 unique operations.");
            foreach (var operation in operations)
            {
                Name(operation.Name);
                if (!ExtensionProtocol.Effects.Contains(operation.Effect) || string.IsNullOrWhiteSpace(operation.Description) || operation.Description.Length > 2048)
                    throw Error("invalid_extension", "Invalid operation effect/description.");
                ExtensionSchema.CheckDefinition(operation.InputSchema);
                if (!operation.InputSchema.TryGetProperty("type", out var kind) || kind.GetString() != "object") throw Error("invalid_extension", "Operation input must be an object schema.");
            }
            var summary = new ExtensionSummary(id, version, Guid.NewGuid().ToString("N"), hash, operations, 0, "active", package.ParallelWorkers);
            // Persist activation only after validation/health. Failure leaves the old generation selected.
            AtomicText(ledger, hash); Save(SafePath(root, id, "active.json"), new ExtensionPointer(version));
            lock (sync)
            {
                if (active.Remove(id, out var old))
                { old.Retired = true; if (old.Leases == 0) DisposeWorker(old.Worker); else retired.Add(old); }
                active.Add(id, new(summary, worker)); worker = null;
                errors.RemoveAll(x => x.StartsWith(id + ":", StringComparison.Ordinal));
            }
            Audit("reload", id, summary.Generation, version); return summary;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
        { throw Error("invalid_extension", ex.Message); }
        finally { if (worker is not null) DisposeWorker(worker); reload.Release(); }
    }

    private (Generation Generation, ExtensionOperation Operation) Find(string id, string name, string generation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!active.TryGetValue(id, out var current)) throw Error("not_found", "Extension is not active.");
        if (current.Summary.Generation != generation) throw Error("stale_generation", "Refresh catalog/description before dispatching the changed extension.");
        return (current, current.Summary.Operations.SingleOrDefault(x => x.Name == name) ?? throw Error("not_found", "Operation is not in this generation."));
    }
    private Generation Pin(string effect, ExtensionCall call)
    {
        lock (sync)
        {
            var (generation, operation) = Find(call.ExtensionId, call.Operation, call.Generation);
            if (operation.Effect != effect) throw Error("effect_mismatch", "Operation requires the " + operation.Effect + " channel.");
            if (call.Arguments.ValueKind != JsonValueKind.Object || call.Arguments.GetRawText().Length > 1024 * 1024) throw Error("invalid_arguments", "Arguments must be an object under 1 MiB.");
            try { ExtensionSchema.Validate(operation.InputSchema, call.Arguments); }
            catch (ArgumentException ex) { throw Error("invalid_arguments", ex.Message); }
            generation.Leases++; return generation;
        }
    }
    private void Release(Generation generation)
    {
        lock (sync)
        { generation.Leases--; if (generation.Retired && generation.Leases == 0) { retired.Remove(generation); DisposeWorker(generation.Worker); } }
    }
    private async Task<JsonElement> InvokePinnedAsync(Generation generation, ExtensionCall call, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ExtensionTimeoutSeconds, 1, 1800)));
        try { return await generation.Worker.InvokeAsync(call.Operation, call.Arguments, timeout.Token); }
        finally { Release(generation); }
    }
    public async Task<JsonElement> InvokeAsync(string effect, ExtensionCall call, CancellationToken ct = default)
    {
        await InitializeAsync(ct); var generation = Pin(effect, call);
        Audit("invoke", call.ExtensionId, call.Generation, call.Operation + ":" + effect);
        return await InvokePinnedAsync(generation, call, ct);
    }
    public async Task<ExtensionJob> StartJobAsync(ExtensionJobRequest request, CancellationToken ct = default)
    {
        await InitializeAsync(ct); Generation generation;
        lock (sync)
        {
            if (jobs.Values.Count(x => x.Job.Status is "queued" or "running") >= 32) throw Error("extension_busy", "At most 32 extension jobs can be active.");
            generation = Pin(request.Effect, request.Call);
        }
        var job = new ExtensionJob(Guid.NewGuid().ToString("N"), request.Call.ExtensionId, request.Call.Operation, request.Call.Generation,
            request.Effect, "queued", DateTimeOffset.UtcNow);
        var cancel = new CancellationTokenSource();
        try { lock (sync) { Save(JobPath(job.Id), job); jobs.Add(job.Id, (job, cancel)); Audit("job_started", job.ExtensionId, job.Generation, job.Id + ":" + job.Operation); } }
        catch { Release(generation); cancel.Dispose(); throw; }
        _ = Task.Run(async () =>
        {
            var dispatched = false;
            ExtensionJob completion;
            try
            {
                Update(job with { Status = "running" });
                dispatched = true;
                var result = await InvokePinnedAsync(generation, request.Call, cancel.Token);
                completion = job with { Status = "succeeded", Result = result };
            }
            catch (Exception ex)
            { completion = job with { Status = "indeterminate", Error = ex.Message + " No rollback or automatic retry was performed." }; }
            finally { if (!dispatched) Release(generation); Audit("job_finished", job.ExtensionId, job.Generation, job.Id); }
            Update(completion);
        });
        return job;
    }
    private void Update(ExtensionJob job)
    {
        lock (sync)
        {
            var runtime = jobs[job.Id]; jobs[job.Id] = (job, runtime.Cancel); Save(JobPath(job.Id), job);
            foreach (var old in jobs.Values.Where(x => x.Job.Status is not ("queued" or "running")).OrderByDescending(x => x.Job.CreatedAt).Skip(100).ToArray())
            { jobs.Remove(old.Job.Id); old.Cancel.Dispose(); }
        }
    }
    public ExtensionJob Job(string id) { lock (sync) return Read<ExtensionJob>(JobPath(id)); }
    public ExtensionJob[] Jobs() { lock (sync) return Directory.GetFiles(state, "job-*.json").Select(Read<ExtensionJob>).OrderByDescending(j => j.CreatedAt).Take(100).ToArray(); }
    public ExtensionJob CancelJob(string id) { lock (sync) { var job = Job(id); if (jobs.TryGetValue(id, out var runtime) && job.Status is "queued" or "running") runtime.Cancel.Cancel(); return job; } }
    private static ExtensionSummary Summary(Generation g) => g.Summary with { ActiveCalls = g.Leases, State = g.Retired ? "draining" : "active" };
    private string JobPath(string id) => Path.Combine(state, "job-" + (Guid.TryParseExact(id, "N", out var guid) ? guid.ToString("N") : throw Error("invalid_id", "Expected job ID.")) + ".json");
    private static string Name(string value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,95}\z") ? value : throw Error("invalid_extension", "Invalid extension/version/file/operation identifier.");
    private static string SafePath(string directory, params string[] parts) { var path = Path.GetFullPath(Path.Combine([directory, ..parts])); ProjectService.RejectLinks(path); return path; }
    private static T Read<T>(string path) { ProjectService.RejectLinks(path); return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), ExtensionProtocol.Json)! : throw Error("not_found", "Extension resource not found: " + Path.GetFileName(path)); }
    private static void AtomicText(string path, string text) { ProjectService.RejectLinks(path); var temp = path + "." + Guid.NewGuid().ToString("N"); File.WriteAllText(temp, text); File.Move(temp, path, true); }
    private static void Save<T>(string path, T value) => AtomicText(path, JsonSerializer.Serialize(value, ExtensionProtocol.Json));
    private void Audit(string action, string id, string generation, string detail)
    { lock (sync) File.AppendAllText(Path.Combine(state, "audit.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, action, extensionId = id, generation, detail }, ExtensionProtocol.Json) + "\n"); }
    private static OperationException Error(string code, string message) => new(code, message);
    private void DisposeWorker(IExtensionWorker worker) { try { worker.Dispose(); } catch (Exception ex) { lock (sync) errors.Add("Worker disposal: " + ex.Message); } }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true;
            foreach (var job in jobs.Values.Where(x => x.Job.Status is "queued" or "running")) job.Cancel.Cancel();
            foreach (var generation in active.Values.Concat(retired)) DisposeWorker(generation.Worker);
        }
    }
}

internal sealed class ProcessExtensionWorker : IExtensionWorker
{
    private readonly Process process;
    private readonly SemaphoreSlim gate = new(1);
    private int closed;
    public ProcessExtensionWorker(string executable, string assembly, string type, string workspace)
    {
        ProjectService.RejectLinks(executable);
        if (!File.Exists(executable)) throw new OperationException("extension_unavailable", "Build/configure ExtensionWorkerPath first.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(assembly)! };
        start.ArgumentList.Add(assembly); start.ArgumentList.Add(type); start.ArgumentList.Add(workspace);
        process = Process.Start(start) ?? throw new IOException("Extension worker did not start.");
        _ = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync() is not null) { } } catch (Exception ex) when (ex is ObjectDisposedException or IOException) { } });
    }
    public async Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct) => (await ExchangeAsync(new("describe"), ct)).Deserialize<ExtensionOperation[]>(ExtensionProtocol.Json)!;
    public Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct) => ExchangeAsync(new("invoke", operation, arguments), ct);
    private async Task<JsonElement> ExchangeAsync(WorkerRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref closed) != 0 || process.HasExited) throw new IOException("Extension worker exited or was interrupted. Reload explicitly; inspect effects before retrying.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, ExtensionProtocol.Json).AsMemory(), ct); await process.StandardInput.FlushAsync(ct);
            var line = await process.StandardOutput.ReadLineAsync(ct) ?? throw new IOException("Extension worker closed without a result.");
            if (line.Length > ExtensionProtocol.MaxReplyCharacters) throw new IOException("Extension result exceeds the 12 MiB character limit.");
            var reply = JsonSerializer.Deserialize<WorkerReply>(line, ExtensionProtocol.Json) ?? throw new IOException("Invalid worker reply.");
            if (!reply.Success) throw new OperationException("extension_operation_failed", reply.Error ?? "Extension failed; inspect effects before retrying.");
            return reply.Value!.Value.Clone();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException)
        { Dispose(); throw new OperationException("extension_indeterminate", "Worker interrupted; inspect effects before retrying: " + ex.Message); }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }
}
