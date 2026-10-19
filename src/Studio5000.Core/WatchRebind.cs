using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record WatchRebindPreviewRequest(int ExpectedRevision, string ProjectId, string ExpectedSha256,
    WatchTemplateProgramMap[]? ProgramMappings = null);
public sealed record WatchRebindApplyRequest(WatchRebindPreviewRequest Preview, string ExpectedReviewSha256);
public sealed record WatchDeclarationShape(string DataType, int[] Dimensions, string BaseDataType, string Sha256, bool Complete);
public sealed record WatchRebindChange(WatchTag Before, WatchTag After, WatchDeclarationShape? BeforeDeclaration,
    WatchDeclarationShape? AfterDeclaration, string[] Changes, string[] Notes);
public sealed record WatchRebindPreviewReport(string WatchId, int FromRevision, string FromExpectedSha256, string? FromObservedSha256,
    string BaselineStatus, string ReviewSha256, BrowserWatchRequest Proposed, bool CanApply, WatchPreflightIssue[] Blockers,
    WatchRebindChange[] Changes, WatchPreflightReport Preflight, string Coverage);

public sealed partial class ProjectService
{
    private WatchDefinition StoppedWatchDefinition(string id, int expectedRevision)
    {
        lock (watchLock)
        {
            var watch = Watch(id);
            if (watch.Definition.Revision != expectedRevision) throw new OperationException("stale_revision", "Watch changed; refresh before rebinding.");
            if (watch.Task is { IsCompleted: false }) throw new OperationException("watch_running", "Stop polling before previewing or applying a rebind.");
            return Clone(watch.Definition)!;
        }
    }

    public async Task<WatchRebindPreviewReport> PreviewWatchRebindAsync(string id, WatchRebindPreviewRequest request, CancellationToken ct = default)
    {
        var definition = StoppedWatchDefinition(id, request.ExpectedRevision); var settings = definition.Settings;
        var template = new WatchTemplate(1, settings.Name, settings.Tags, settings.IntervalMs, settings.HistoryLimit, settings.Backend);
        var binding = await PreviewWatchTemplateAsync(new(JsonSerializer.SerializeToElement(template, Json), request.ProjectId, request.ExpectedSha256,
            ProgramMappings: request.ProgramMappings, Mode: settings.Mode, ControllerTargetId: settings.ControllerTargetId), ct);
        var baseline = await ReadRebindBaselineAsync(settings, ct);
        var destination = Get(request.ProjectId); Expect(destination, request.ExpectedSha256);
        var destinationBytes = await File.ReadAllBytesAsync(RevisionFile(destination.Id, destination.Revision), ct);
        if (Hash(destinationBytes) != destination.CurrentSha256) throw new OperationException("stale_revision", "Destination snapshot bytes changed.");
        var document = XmlProjects.Parse(destinationBytes);
        var beforeContext = WatchShapeContext(baseline.Document); var afterContext = WatchShapeContext(document);
        if (baseline.Status == "matching_l5x" && beforeContext is null) baseline = ("invalid_metadata", baseline.ObservedSha256, null);
        var changes = settings.Tags.Zip(binding.Proposed.Tags).Select(pair => CompareWatchDeclarations(beforeContext, afterContext, pair.First, pair.Second)).ToArray();
        var review = Hash(JsonSerializer.SerializeToUtf8Bytes(new { watchId = definition.Id, definition.Revision, before = settings,
            destination = binding.ReviewSha256, baseline.Status, baseline.ObservedSha256 }, Json));
        return new(definition.Id, definition.Revision, settings.ExpectedSha256, baseline.ObservedSha256, baseline.Status, review,
            binding.Proposed, binding.CanImport, binding.ImportBlockers, changes, binding.Preflight,
            "Saved declarations only; no SDK project, reader or controller connection. Missing/changed/ACD baseline metadata remains unverified and is never treated as the approved old schema. Fingerprints cover selected type/dimensions and reachable base-tag UDT declarations, excluding descriptions/stored values. Unknown templates remain partial. Apply rechecks the watch revision/review hash and destination; retains the list ID, settings and recording history, creates a new source artifact/revision and leaves polling stopped.");
    }

    public async Task<WatchStatus> ApplyWatchRebindAsync(string id, WatchRebindApplyRequest request, CancellationToken ct = default)
    {
        var preview = await PreviewWatchRebindAsync(id, request.Preview, ct);
        if (!preview.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "Rebind differs from the reviewed preview. Preview again before applying.");
        if (!preview.CanApply) throw new OperationException("watch_rebind_binding", "Resolve destination binding diagnostics before rebinding.");
        await gate.WaitAsync(ct);
        try
        {
            var project = Get(preview.Proposed.ProjectId); Expect(project, preview.Proposed.ExpectedSha256);
            var source = RevisionFile(project.Id, project.Revision); var bytes = await File.ReadAllBytesAsync(source, ct);
            if (Hash(bytes) != project.CurrentSha256) throw new OperationException("stale_revision", "Destination snapshot bytes changed.");
            lock (watchLock)
            {
                var definition = StoppedWatchDefinition(id, request.Preview.ExpectedRevision);
                var artifactId = Guid.NewGuid().ToString("N");
                var settings = definition.Settings with { Tags = preview.Proposed.Tags, SourcePath = null, SourceArtifactId = artifactId, ExpectedSha256 = project.CurrentSha256 };
                WatchValidation.Validate(settings); var target = WatchTarget(settings); ValidateWatchPolicy(settings, target); ValidateCipWatch(settings, target, source);
                ct.ThrowIfCancellationRequested();
                var dir = ArtifactDir(artifactId); Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "project.l5x"), bytes);
                Save(Path.Combine(dir, "artifact.json"), new Artifact(artifactId, project.Id, "l5x", project.CurrentSha256, bytes.Length, "Reviewed watch rebind source snapshot; saved metadata only."));
                var result = PersistWatchLocked(settings, id, definition.Revision);
                Watch(id).Recorder.Append(result.Definition.Revision, "", "source_rebound", detail:
                    $"Watch revision {definition.Revision} -> {result.Definition.Revision}; source {definition.Settings.ExpectedSha256} -> {project.CurrentSha256}; review {preview.ReviewSha256}", force: true);
                return result;
            }
        }
        finally { gate.Release(); }
    }

    private async Task<(string Status, string? ObservedSha256, XDocument? Document)> ReadRebindBaselineAsync(WatchListRequest settings, CancellationToken ct)
    {
        try
        {
            var source = WatchSource(settings);
            await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            if (file.Length > 64L * 1024 * 1024 || !Path.GetExtension(source).Equals(".l5x", StringComparison.OrdinalIgnoreCase))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
                return (hash.Equals(settings.ExpectedSha256, StringComparison.OrdinalIgnoreCase) ? "metadata_deferred" : "source_changed", hash, null);
            }
            var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, ct); var observed = Hash(bytes);
            if (!observed.Equals(settings.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) return ("source_changed", observed, null);
            try { return ("matching_l5x", observed, XmlProjects.Parse(bytes)); }
            catch (Exception ex) when (ex is OperationException or XmlException) { return ("invalid_metadata", observed, null); }
        }
        catch (Exception ex) when (ex is OperationException or IOException or UnauthorizedAccessException) { return ("source_unavailable", null, null); }
    }

    private sealed record ShapeContext(XDocument Document, IReadOnlyDictionary<string, StructureType> Types);
    private static ShapeContext? WatchShapeContext(XDocument? document)
    {
        if (document is null) return null;
        try { return new(document, LogixStructures.Types(document).ToDictionary(t => t.Name, StringComparer.Ordinal)); }
        catch (Exception ex) when (ex is OperationException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static WatchRebindChange CompareWatchDeclarations(ShapeContext? before, ShapeContext? after, WatchTag oldTag, WatchTag newTag)
    {
        var changes = new List<string>(); var notes = new List<string>();
        WatchDeclarationShape? Shape(ShapeContext? context, WatchTag tag, string side)
        {
            if (context is null) { notes.Add(side + ": approved source declarations unavailable."); return null; }
            try { return WatchShape(context, tag); }
            catch (Exception ex) when (ex is OperationException or ArgumentException or InvalidOperationException)
            { notes.Add(side + ": declaration comparison unavailable: " + (ex is OperationException operation ? operation.Code + ": " : "") + ex.Message[..Math.Min(512, ex.Message.Length)]); return null; }
        }
        var oldShape = Shape(before, oldTag, "before"); var newShape = Shape(after, newTag, "after");
        if (oldTag.ProgramName != newTag.ProgramName) changes.Add("scope_changed");
        if (oldShape is null) changes.Add("before_unavailable");
        if (newShape is null) changes.Add("after_unavailable");
        if (oldShape is not null && newShape is not null)
        {
            if (oldShape.DataType != newShape.DataType) changes.Add("selected_type_changed");
            if (!oldShape.Dimensions.SequenceEqual(newShape.Dimensions)) changes.Add("selected_dimensions_changed");
            if (oldShape.Sha256 != newShape.Sha256) changes.Add("base_declaration_changed");
            if (!oldShape.Complete || !newShape.Complete) changes.Add("declarations_partial");
        }
        if (changes.Count == 0) changes.Add("unchanged");
        return new(oldTag, newTag, oldShape, newShape, changes.ToArray(), notes.ToArray());
    }

    private static WatchDeclarationShape WatchShape(ShapeContext context, WatchTag tag)
    {
        var document = context.Document; var types = context.Types;
        var selected = ResolveWatchDeclaration(document, types, tag); var root = LogixStructures.Tag(document, tag.TagName, tag.ProgramName);
        var baseType = (string?)root.Attribute("DataType") ?? "Unknown"; var declarations = new SortedDictionary<string, StructureType>(StringComparer.Ordinal);
        var complete = true; var nodes = 0; var active = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string type, int depth)
        {
            if (++nodes > 8192 || depth > 16) throw new OperationException("watch_rebind_limit", "Declaration comparison exceeds its node/depth budget.");
            if (string.IsNullOrWhiteSpace(type) || type.Length > 256) throw new OperationException("watch_rebind_declaration", "Invalid declared type name.");
            if (type is "BOOL" or "BIT" or "SINT" or "INT" or "DINT" or "LINT" or "USINT" or "UINT" or "UDINT" or "ULINT" or "REAL" or "LREAL") return;
            if (!types.TryGetValue(type, out var declaration)) { complete = false; return; }
            if (active.Contains(type)) { complete = false; return; }
            if (!declarations.TryAdd(type, declaration)) return;
            active.Add(type);
            foreach (var member in declaration.Members) Visit(member.DataType, depth + 1);
            active.Remove(type);
        }
        Visit(baseType, 0);
        var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { baseType, baseDimensions = LogixStructures.Dimensions((string?)root.Attribute("Dimensions")),
            externalAccess = (string?)root.Attribute("ExternalAccess"), selectedType = selected.Type, selectedDimensions = selected.Dimensions, declarations }, Json));
        return new(selected.Type, selected.Dimensions, baseType, hash, complete);
    }
}
