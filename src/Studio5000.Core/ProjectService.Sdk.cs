using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cancel, Task Task)> sdkJobs = new();
    private readonly object sdkJobLock = new();
    private string SdkDirectory => Path.Combine(state, "sdk-jobs");
    private string SdkJobPath(string id) => Path.Combine(SdkDirectory, Id(id), "job.json");

    private void InitializeSdkJobs()
    {
        Directory.CreateDirectory(SdkDirectory);
        foreach (var file in Directory.GetFiles(SdkDirectory, "job.json", SearchOption.AllDirectories))
        {
            var job = Read<SdkJob>(file);
            if (job.Status is "queued" or "running") Save(file, job with { Status = "indeterminate", Error = "Service restarted before completion was recorded. Inspect any external target before retrying." });
        }
    }
    public object SdkCapabilities() => new
    {
        package = "RockwellAutomation.LogixDesigner.CSClient", version = "2.1.974", nativeBridgeConfigured = native.Available,
        publicSignatures = SdkCatalog.Surface.Methods.Length, operationCount = SdkCatalog.Methods.Length,
        methods = SdkCatalog.Describe(), enums = SdkCatalog.Surface.Enums,
        onlineEdits = OnlineEdits.Capabilities(),
        managedMethods = SdkCatalog.ManagedMethods, controllerOperationsEnabled = options.EnableControllerOperations,
        controllerTargets = options.ControllerTargets.Select(t => new { t.Id, t.CommunicationsPath }),
        persistence = "New artifacts and a new managed result project; source files and source revisions are preserved.",
        validation = "Compile-checked bindings for SDK 2.1.974. Native offline verification is operation-specific; live controller verification is pending."
    };
    public Task<JsonElement> SdkCoverageAsync(CancellationToken ct = default) => native.CoverageAsync(ct);
    public async Task<object> InspectSdkSourceAsync(string path, CancellationToken ct = default)
    {
        path = AllowedSource(path);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".acd" or ".l5x" or ".l5k" or ".xml"))
            throw new OperationException("sdk_source_invalid", "Choose an ACD/L5X/L5K source or XML import file.");
        if (new FileInfo(path).Length > 500L * 1024 * 1024) throw new OperationException("file_too_large", "SDK input exceeds 500 MiB.");
        return new { path, sha256 = await SdkFileHashAsync(path, ct), bytes = new FileInfo(path).Length };
    }
    public SdkJob SdkJob(string id) { lock (sdkJobLock) return Read<SdkJob>(SdkJobPath(id)); }
    public SdkJob[] SdkJobList() { lock (sdkJobLock) return Directory.GetFiles(SdkDirectory, "job.json", SearchOption.AllDirectories)
        .Select(Read<SdkJob>).OrderByDescending(j => j.CreatedAt).Take(100).ToArray(); }
    public SdkJob CancelSdkJob(string id)
    {
        var job = SdkJob(id);
        lock (sdkJobLock) if (sdkJobs.TryGetValue(id, out var active)) active.Cancel.Cancel();
        return job; // Poll for the terminal result. Cancellation is not rollback.
    }

    public async Task<SdkJob> StartSdkJobAsync(SdkRunRequest request, CancellationToken ct = default)
    {
        SdkCatalog.ValidateCalls(request.Calls);
        if (!native.Available) throw new OperationException("native_unavailable", "Build and configure NativeBridgePath before submitting SDK jobs.");
        var calls = request.Calls.Select(c => new SdkCall(c.Method, new(c.Arguments))).ToArray();
        var controller = SdkCatalog.NeedsController(calls);
        var target = request.ControllerTargetId is null ? null : options.ControllerTargets.SingleOrDefault(t => t.Id == request.ControllerTargetId)
            ?? throw new OperationException("sdk_target_unknown", "Select a configured ControllerTargetId.");
        if (controller && (!options.EnableControllerOperations || target is null))
            throw new OperationException("sdk_controller_disabled", "Controller operations require EnableControllerOperations and a configured ControllerTargetId.");
        if (calls.Any(c => c.Method == "SetCommunicationsPathAsync") && target is null)
            throw new OperationException("sdk_target_required", "SetCommunicationsPathAsync requires a configured ControllerTargetId.");
        if (new[] { request.ProjectId, request.SourcePath, request.SourceArtifactId }.Count(s => s is not null) > 1)
            throw new OperationException("sdk_source_invalid", "Choose exactly one projectId, sourcePath or sourceArtifactId.");
        var factory = SdkCatalog.Factories.Contains(calls[0].Method) ? calls[0].Method : null;
        var hasSource = request.ProjectId is not null || request.SourcePath is not null || request.SourceArtifactId is not null;
        if (factory is "CreateNewProjectAsync" or "UploadToNewProjectAsync" && hasSource)
            throw new OperationException("sdk_source_invalid", "Create/upload-to-new must have no source project.");
        if (!hasSource && factory is not ("CreateNewProjectAsync" or "UploadToNewProjectAsync") && calls.Any(c => !SdkCatalog.Find(c.Method).IsStatic || SdkCatalog.Factories.Contains(c.Method)))
            throw new OperationException("sdk_source_required", "Supply a source project or begin with CreateNewProjectAsync/UploadToNewProjectAsync.");
        var id = Guid.NewGuid().ToString("N"); var dir = Path.Combine(SdkDirectory, id); var work = Path.Combine(dir, "work");
        Directory.CreateDirectory(work);
        string? source = null, hash = null;
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await gate.WaitAsync(ct);
        try
        {
            if (hasSource)
            {
                string input;
                if (request.ProjectId is not null)
                {
                    var info = Get(request.ProjectId); Expect(info, request.ExpectedSha256!);
                    if (info.Kind != "logix") throw new OperationException("sdk_project_kind", "The PLC SDK requires a Logix project.");
                    input = RevisionFile(info.Id, info.Revision);
                }
                else input = request.SourceArtifactId is not null ? Download(request.SourceArtifactId).Path : AllowedSource(request.SourcePath!);
                var extension = Path.GetExtension(input).ToLowerInvariant();
                if (extension is not (".acd" or ".l5x" or ".l5k")) throw new OperationException("sdk_source_invalid", "SDK source must be ACD, L5X or L5K.");
                source = Path.Combine(work, "input" + extension);
                hash = await SnapshotSdkFileAsync(input, source, request.ExpectedSha256, ct);
            }
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in request.InputFiles ?? [])
            {
                if (file.Value is null || (file.Value.Path is null) == (file.Value.ArtifactId is null))
                    throw new OperationException("sdk_input_invalid", "Each inputFiles alias needs exactly one path or artifactId, plus its SHA-256.");
                var input = file.Value.ArtifactId is not null ? Download(file.Value.ArtifactId).Path : AllowedSource(file.Value.Path!);
                if (Path.GetExtension(input).ToLowerInvariant() is not (".l5x" or ".xml")) throw new OperationException("sdk_input_invalid", "Partial import files must be XML/L5X.");
                var output = Path.Combine(work, "import-" + inputs.Count + ".l5x");
                await SnapshotSdkFileAsync(input, output, file.Value.Sha256, ct);
                using (var reader = XmlReader.Create(output, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 500L * 1024 * 1024 }))
                    while (reader.Read()) { }
                inputs.Add(file.Key, output);
            }
            foreach (var call in calls)
            {
                void Put(string key, string value) => call.Arguments[key] = JsonSerializer.SerializeToElement(value);
                if (SdkCatalog.Factories.Contains(call.Method))
                {
                    if (call.Method is "OpenLogixProjectAsync" or "ConvertAsync")
                    {
                        if (source is null) throw new OperationException("sdk_source_required", "Open/convert requires a source project.");
                        Put(call.Method == "ConvertAsync" ? "projectPath" : "projectFilePath", source);
                    }
                    else Put("projectFilePath", Path.Combine(work, "created.acd"));
                }
                if (call.Method is "SetCommunicationsPathAsync" or "UploadToNewProjectAsync")
                    Put(call.Method == "SetCommunicationsPathAsync" ? "projectPath" : "commPath", target!.CommunicationsPath);
                if (call.Method is "SaveAsAsync" or "PartialExportToXmlFileAsync")
                {
                    var key = call.Method == "SaveAsAsync" ? "savePath" : "filePath";
                    var name = call.Arguments.TryGetValue(key, out var value) ? value.GetString()! : "export-" + outputs.Count + (key == "savePath" ? ".acd" : ".l5x");
                    ValidateFileName(name);
                    var extension = Path.GetExtension(name).ToLowerInvariant();
                    if (name != Path.GetFileName(name) || extension is not (".acd" or ".l5x" or ".l5k" or ".xml") ||
                        key == "savePath" && extension == ".xml" ||
                        key == "filePath" && extension is not (".xml" or ".l5x") ||
                        name.StartsWith("input.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("import-", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("created.acd", StringComparison.OrdinalIgnoreCase) || name.StartsWith("final.", StringComparison.OrdinalIgnoreCase) || !outputs.Add(name))
                        throw new OperationException("sdk_output_invalid", "Choose a unique simple ACD/L5X/L5K filename (XML/L5X for partial exports); input/import/created/final names are reserved.");
                    Put(key, Path.Combine(work, name));
                }
                if (SdkCatalog.Find(call.Method).Parameters.Any(p => p.Name == "xmlFileToImport"))
                {
                    if (!call.Arguments.TryGetValue("xmlFileToImport", out var value) || !inputs.TryGetValue(value.GetString()!, out var input))
                        throw new OperationException("sdk_input_required", "xmlFileToImport must name an inputFiles alias with an absolute path and SHA-256.");
                    Put("xmlFileToImport", input);
                }
            }
            SdkCatalog.ValidateCalls(calls);
            var job = new SdkJob(id, "queued", DateTimeOffset.UtcNow, calls.Select(c => c.Method).ToArray(), hash, target?.Id, controller, [], [], []);
            Save(SdkJobPath(id), job);
            var cancel = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // All file/target checks and snapshots complete before any proprietary call is dispatched.
            var task = Task.Run(async () =>
            {
                await ready.Task;
                try { await RunSdkJobAsync(job, new(work, source, target?.CommunicationsPath, calls), outputs, cancel.Token); }
                finally { lock (sdkJobLock) { sdkJobs.TryRemove(id, out _); cancel.Dispose(); } }
            });
            lock (sdkJobLock) sdkJobs[id] = (cancel, task);
            ready.SetResult();
            Audit("sdk_queued", SdkAuditProject(job), hash, null, string.Join(",", job.Methods));
            return job;
        }
        finally { gate.Release(); }
    }

    private static async Task<string> SnapshotSdkFileAsync(string input, string output, string? expected, CancellationToken ct)
    {
        if (new FileInfo(input).Length > 500L * 1024 * 1024) throw new OperationException("file_too_large", "SDK input exceeds 500 MiB.");
        File.Copy(input, output);
        var hash = await SdkFileHashAsync(output, ct);
        if (expected is null || !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "Supply the exact current SHA-256 of the SDK input.");
        return hash;
    }
    private static async Task<string> SdkFileHashAsync(string path, CancellationToken ct = default)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant(); }

    private async Task RunSdkJobAsync(SdkJob initial, SdkBridgeRequest request, HashSet<string> outputs, CancellationToken ct)
    {
        var job = initial with { Status = "running" };
        void Persist() { lock (sdkJobLock) Save(SdkJobPath(job.Id), job); }
        Persist();
        try
        {
            var result = await native.ExecuteSdkAsync(request, ct);
            job = job with { Status = result.Status, Steps = result.Steps, Events = result.Events, Error = result.Error };
            if (result.Status == "succeeded")
            {
                var l5x = Path.Combine(request.WorkDirectory, "final.l5x");
                if (File.Exists(l5x) && new FileInfo(l5x).Length <= 64L * 1024 * 1024)
                {
                    var bytes = await File.ReadAllBytesAsync(l5x, CancellationToken.None); XmlProjects.Parse(bytes);
                    var id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(ProjectDir(id));
                    File.Copy(Path.Combine(request.WorkDirectory, "final.acd"), Path.Combine(ProjectDir(id), "original.acd"));
                    File.Copy(l5x, RevisionFile(id, 0));
                    var original = await SdkFileHashAsync(Path.Combine(ProjectDir(id), "original.acd"));
                    var info = new ProjectInfo(id, "SDK-" + job.Id[..8], "sdk-job:" + job.Id, original, Hash(bytes), 0, DateTimeOffset.UtcNow);
                    Save(ProjectMeta(id), info); job = job with { ResultProject = info };
                }
                if (File.Exists(l5x))
                {
                    outputs.Add("final.acd"); outputs.Add("final.l5x");
                    if (job.ResultProject is null) job = job with { Events = [..job.Events, new("status", "Native outputs exceed the 64 MiB managed XML editor limit; artifacts remain available for further SDK jobs.")] };
                }
                var artifacts = new List<Artifact>();
                foreach (var name in outputs)
                {
                    var file = Path.Combine(request.WorkDirectory, name);
                    if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new IOException("SDK did not produce a nonempty requested output: " + name);
                    var id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(ArtifactDir(id));
                    var format = Path.GetExtension(name)[1..].ToLowerInvariant();
                    File.Copy(file, Path.Combine(ArtifactDir(id), "project." + format));
                    var artifact = new Artifact(id, job.ResultProject?.Id ?? job.Id, format,
                        await SdkFileHashAsync(file), new FileInfo(file).Length,
                        "SDK job " + job.Id + " output " + name + "; SDK call completion is not controller/runtime acceptance.");
                    Save(Path.Combine(ArtifactDir(id), "artifact.json"), artifact); artifacts.Add(artifact);
                }
                job = job with { Artifacts = artifacts.ToArray() };
            }
        }
        catch (OperationCanceledException) { job = job with { Status = "cancelled", Error = "Cancelled before the SDK process was started." }; }
        catch (Exception ex)
        { job = job with { Status = "failed", Error = "SDK job processing failed: " + ex.GetType().Name + ". Inspect retained job files; requests are never retried automatically." }; }
        finally
        {
            Persist(); Audit("sdk_" + job.Status, SdkAuditProject(job), job.SourceSha256, job.ResultProject?.CurrentSha256, job.Id);
        }
    }
    private static ProjectInfo SdkAuditProject(SdkJob job) => job.ResultProject ?? new(job.Id, "SDK job", "sdk-job:" + job.Id,
        job.SourceSha256 ?? "", job.SourceSha256 ?? "", 0, job.CreatedAt);
    private void DisposeSdkJobs()
    {
        Task[] tasks;
        lock (sdkJobLock) { foreach (var active in sdkJobs.Values) active.Cancel.Cancel(); tasks = sdkJobs.Values.Select(a => a.Task).ToArray(); }
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
    }
}
