using System.Diagnostics;
using System.Text.Json;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private sealed class WatchRuntime(WatchDefinition definition, string directory)
    {
        public WatchDefinition Definition = definition;
        public readonly WatchRecorder Recorder = new(directory, definition);
        public string Session = "";
        public string State = "stopped";
        public string? Error;
        public WatchSnapshot? Latest;
        public StructureType[] Types = [];
        public readonly Queue<WatchSnapshot> History = new();
        public long HistoryBytes;
        public CancellationTokenSource? Cancel;
        public Task? Task;
    }
    private readonly object watchLock = new();
    private readonly Dictionary<string, WatchRuntime> watches = new();
    private string WatchDirectory => Path.Combine(state, "watches");
    private string WatchDir(string id) => Path.Combine(WatchDirectory, Id(id));
    private void InitializeWatches()
    {
        Directory.CreateDirectory(WatchDirectory);
        foreach (var file in Directory.GetFiles(WatchDirectory, "definition.json", SearchOption.AllDirectories))
        {
            var definition = Read<WatchDefinition>(file); WatchValidation.Validate(definition.Settings);
            var runtime = new WatchRuntime(definition, WatchDir(definition.Id));
            var latest = Path.Combine(Path.GetDirectoryName(file)!, "latest.json");
            if (File.Exists(latest)) runtime.Latest = Read<WatchSnapshot>(latest);
            foreach (var sample in runtime.Recorder.Query(new(), definition.Revision).Where(r => r.Snapshot is not null).Select(r => r.Snapshot!))
                CacheSample(runtime, sample);
            runtime.Recorder.Append(definition.Revision, "", "service_restart", detail: "Polling remains stopped after service startup.");
            watches.Add(definition.Id, runtime); // Always stopped, including after an interrupted online session.
        }
    }
    private WatchRuntime Watch(string id) => watches.TryGetValue(Id(id), out var watch) ? watch : throw new OperationException("not_found", "Watch list not found.");
    private WatchStatus Status(WatchRuntime watch) => new(Clone(watch.Definition)!, watch.State, Clone(watch.Latest),
        watch.State != "running" || watch.Latest is null || (DateTimeOffset.UtcNow - watch.Latest.CompletedAt).TotalMilliseconds > Math.Max(watch.Definition.Settings.IntervalMs * 2L, 1000),
        watch.Error, Clone(watch.Types)!);
    private static T? Clone<T>(T? value) => value is null ? default : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
    public WatchStatus[] WatchLists() { lock (watchLock) return watches.Values.Select(Status).ToArray(); }
    public WatchStatus GetWatch(string id) { lock (watchLock) return Status(Watch(id)); }
    public WatchSnapshot[] WatchHistory(string id, int limit = 100)
    { lock (watchLock) return Clone(Watch(id).History.TakeLast(Math.Clamp(limit, 1, 1000)).ToArray())!; }

    public async Task<WatchStatus> SaveWatchAsync(WatchListRequest request, string? id = null, int? expectedRevision = null, CancellationToken ct = default)
    {
        WatchValidation.Validate(request);
        var target = WatchTarget(request); ValidateWatchPolicy(request, target);
        var source = WatchSource(request);
        if (!string.Equals(await SdkFileHashAsync(source, ct), request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "Watch source hash changed.");
        lock (watchLock)
        {
            WatchRuntime runtime;
            if (id is null)
            {
                if (watches.Count >= 100) throw new OperationException("watch_limit", "Maximum 100 saved watch lists.");
                id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(WatchDir(id));
                runtime = new(new(id, 0, DateTimeOffset.UtcNow, Clone(request)!), WatchDir(id)); watches.Add(id, runtime);
            }
            else
            {
                runtime = Watch(id);
                if (runtime.Task is { IsCompleted: false }) throw new OperationException("watch_running", "Stop the list before editing it.");
                if (runtime.Definition.Revision != expectedRevision) throw new OperationException("stale_revision", "Watch list changed; refresh it before saving.");
                runtime.Definition = runtime.Definition with { Settings = Clone(request)!, Revision = runtime.Definition.Revision + 1 };
                runtime.Latest = null; runtime.History.Clear(); runtime.HistoryBytes = 0; runtime.Types = []; runtime.Error = null; runtime.State = "stopped";
                var old = Path.Combine(WatchDir(id), "latest.json"); if (File.Exists(old)) File.Delete(old);
            }
            Save(Path.Combine(WatchDir(id), "definition.json"), runtime.Definition);
            runtime.Recorder.PreserveDefinition(runtime.Definition);
            return Status(runtime);
        }
    }

    private ControllerTarget? WatchTarget(WatchListRequest request) => request.ControllerTargetId is null ? null :
        options.ControllerTargets.SingleOrDefault(t => t.Id == request.ControllerTargetId) ?? throw new OperationException("sdk_target_unknown", "Select a configured controller target.");
    private static void ValidateWatchPolicy(WatchListRequest request, ControllerTarget? target)
    {
        var calls = request.Tags.Select(t => new SdkCall("GetTagValueAsync", new()
        { ["tagPath"] = JsonSerializer.SerializeToElement(WatchValidation.TagPath(t)), ["mode"] = JsonSerializer.SerializeToElement(request.Mode), ["type"] = JsonSerializer.SerializeToElement("BYTE_ARRAY") })).ToList();
        if (request.Mode == "Online") calls.Insert(0, new("GoOnlineAsync", []));
        SdkTargetPolicies.ValidateCalls(target, calls.ToArray());
    }
    private string WatchSource(WatchListRequest request)
    {
        var path = request.SourceArtifactId is null ? AllowedSource(request.SourcePath!) : Download(request.SourceArtifactId).Path;
        if (Path.GetExtension(path).ToLowerInvariant() is not (".acd" or ".l5x")) throw new OperationException("watch_source", "Select a saved ACD or L5X source.");
        if (new FileInfo(path).Length > 500L * 1024 * 1024) throw new OperationException("file_too_large", "Watch source exceeds 500 MiB.");
        return path;
    }

    public Task<WatchStatus> StartWatchAsync(string id)
    {
        lock (watchLock)
        {
            var watch = Watch(id); var request = watch.Definition.Settings;
            if (watch.Task is { IsCompleted: false }) return Task.FromResult(Status(watch));
            var target = WatchTarget(request); ValidateWatchPolicy(request, target);
            if (request.Mode == "Online" && (!options.EnableControllerOperations || target is null))
                throw new OperationException("sdk_controller_disabled", "Online watch polling requires enabled controller access and a configured target.");
            if (watches.Values.Count(w => w.Task is { IsCompleted: false }) >= 4) throw new OperationException("watch_limit", "Maximum four active watch lists.");
            watch.Cancel?.Dispose(); watch.Cancel = new(); watch.State = "starting"; watch.Error = null;
            watch.Session = Guid.NewGuid().ToString("N");
            watch.Recorder.Append(watch.Definition.Revision, watch.Session, "start");
            var token = watch.Cancel.Token;
            watch.Task = Task.Run(() => PollWatchAsync(watch, target, token));
            return Task.FromResult(Status(watch));
        }
    }

    private async Task PollWatchAsync(WatchRuntime watch, ControllerTarget? target, CancellationToken ct)
    {
        var settings = watch.Definition.Settings;
        try
        {
            var dir = Path.Combine(WatchDir(watch.Definition.Id), "runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var input = WatchSource(settings); var snapshot = Path.Combine(dir, "input" + Path.GetExtension(input));
            await SnapshotSdkFileAsync(input, snapshot, settings.ExpectedSha256, ct);
            await using var session = await watchFactory.OpenAsync(new(dir, snapshot, settings.Mode == "Online" ? target?.CommunicationsPath : null, settings.Mode), ct);
            var metadata = XmlProjects.Parse(await File.ReadAllBytesAsync(Path.Combine(dir, "metadata.l5x"), ct));
            foreach (var tag in settings.Tags)
            {
                var definition = LogixStructures.Tag(metadata, tag.TagName, tag.ProgramName);
                if ((string?)definition.Attribute("TagType") != "Base") throw new OperationException("watch_alias", "Watch lists read exact base tags; select an alias's authorized base explicitly.");
            }
            lock (watchLock) { watch.Types = LogixStructures.Types(metadata); watch.State = "running"; }
            while (true)
            {
                ct.ThrowIfCancellationRequested(); var clock = Stopwatch.StartNew(); var start = DateTimeOffset.UtcNow;
                var reply = await session.ReadAsync(settings.Tags, ct);
                if (!reply.Success) throw new OperationException("watch_reader", reply.Error ?? "Reader failed.");
                var values = settings.Tags.Select(t =>
                {
                    var result = reply.Tags.SingleOrDefault(v => v.Name == t.TagName && v.ProgramName == t.ProgramName);
                    if (result is null) return new WatchValue(t.Key, t.TagName, t.ProgramName, "Unknown", null, "bad", "Reader omitted this tag.");
                    try { return new WatchValue(t.Key, t.TagName, t.ProgramName, result.DataType,
                        result.Quality == "good" ? LogixStructures.Select(result.Value, t.MemberPath) : null, result.Quality, result.Error); }
                    catch (OperationException ex) { return new WatchValue(t.Key, t.TagName, t.ProgramName, result.DataType, null, "bad", ex.Message); }
                }).ToArray();
                lock (watchLock)
                {
                    var sample = new WatchSnapshot((watch.Latest?.Sequence ?? 0) + 1, start, DateTimeOffset.UtcNow, settings.Mode, values);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(sample, Json).Length;
                    if (bytes > 4 * 1024 * 1024) throw new OperationException("watch_too_large", "A sample exceeds 4 MiB; reduce the list.");
                    watch.Recorder.Append(watch.Definition.Revision, watch.Session, "sample", sample);
                    watch.Latest = sample; CacheSample(watch, sample);
                    Save(Path.Combine(WatchDir(watch.Definition.Id), "latest.json"), sample);
                }
                var remaining = settings.IntervalMs - clock.ElapsedMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { lock (watchLock) watch.State = "stopped"; }
        catch (Exception ex) { lock (watchLock) { watch.State = "faulted"; watch.Error = ex.Message; } }
        finally
        {
            lock (watchLock)
            {
                try { watch.Recorder.Append(watch.Definition.Revision, watch.Session, watch.State == "faulted" ? "fault" : "stop", detail: watch.Error); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { watch.State = "faulted"; watch.Error = "Recording failed: " + ex.Message; }
            }
        }
    }

    private static void CacheSample(WatchRuntime watch, WatchSnapshot sample)
    {
        watch.History.Enqueue(sample); watch.HistoryBytes += JsonSerializer.SerializeToUtf8Bytes(sample, Json).Length;
        while (watch.History.Count > watch.Definition.Settings.HistoryLimit || watch.HistoryBytes > 8 * 1024 * 1024)
            watch.HistoryBytes -= JsonSerializer.SerializeToUtf8Bytes(watch.History.Dequeue(), Json).Length;
    }

    public async Task<WatchStatus> StopWatchAsync(string id)
    {
        Task? task;
        lock (watchLock) { var watch = Watch(id); watch.Cancel?.Cancel(); task = watch.Task; }
        if (task is not null) await task;
        lock (watchLock) { var watch = Watch(id); watch.State = "stopped"; return Status(watch); }
    }
    public void DeleteWatch(string id)
    {
        lock (watchLock)
        {
            var watch = Watch(id); if (watch.Task is { IsCompleted: false }) throw new OperationException("watch_running", "Stop the watch list before deleting it.");
            // Keep source snapshots/evidence in ignored state; removing the definition removes it from the catalog.
            File.Delete(Path.Combine(WatchDir(id), "definition.json")); watches.Remove(id); watch.Cancel?.Dispose();
        }
    }
    private void DisposeWatches()
    {
        Task[] tasks;
        lock (watchLock) { foreach (var watch in watches.Values) watch.Cancel?.Cancel(); tasks = watches.Values.Select(w => w.Task).OfType<Task>().ToArray(); }
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
    }
}
