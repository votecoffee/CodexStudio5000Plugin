using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Studio5000.Core;

public sealed record DiagnosticTriggerCondition(string Kind, string? Key = null, string? MemberPath = null, string Operator = "eq", JsonElement? Value = null, int HoldMs = 1000);
public sealed record DiagnosticTriggerSaveRequest(string Name, string ProjectId, string ExpectedSha256, string WatchId, int ExpectedWatchRevision,
    DiagnosticTriggerCondition Condition, int PreSamples = 20, int PostSamples = 20, int RetainEpisodes = 10, string? Id = null, int? ExpectedRevision = null);
public sealed record DiagnosticTriggerDefinition(string Id, int Revision, string Name, string ProjectId, int ProjectRevision, string ProjectSha256,
    WatchDefinition Watch, DiagnosticTriggerCondition Condition, int PreSamples, int PostSamples, int RetainEpisodes);
public sealed record DiagnosticTriggerArmRequest(int ExpectedRevision, bool Armed = true);
public sealed record DiagnosticFrame(DateTimeOffset At, string Kind, WatchSnapshot? Sample, bool Trigger, string? Detail = null);
public sealed record DiagnosticEpisode(string Id, DiagnosticTriggerDefinition Definition, string Session, DateTimeOffset TriggeredAt, string Status,
    DiagnosticFrame[] Frames, int DroppedPreSamples, string[] Diagnostics, Artifact? Artifact = null);
public sealed record DiagnosticEpisodeSummary(string Id, DateTimeOffset TriggeredAt, string Status, int Frames, Artifact Artifact);
public sealed record DiagnosticTriggerStatus(DiagnosticTriggerDefinition Definition, string State, int BufferedSamples, DiagnosticEpisodeSummary[] Episodes, string? Error);
public sealed record DiagnosticEpisodeCaptureRequest(string ExpectedEpisodeSha256, int FrameIndex = -1);

public sealed partial class ProjectService
{
    private sealed class TriggerRuntime(DiagnosticTriggerDefinition definition)
    {
        internal DiagnosticTriggerDefinition Definition = definition;
        internal string State = "disarmed", Session = "";
        internal string? Error;
        internal readonly Queue<DiagnosticFrame> Buffer = new();
        internal long BufferBytes;
        internal DiagnosticEpisode? Pending;
        internal int PostRemaining, Dropped;
        internal JsonNode? Previous;
        internal DateTimeOffset? Since, LastAt;
    }
    private readonly Dictionary<string, TriggerRuntime> triggers = new(StringComparer.Ordinal);
    private string TriggerDir(string id) => Path.Combine(ReviewDirectory("diagnostic-triggers"), Id(id));
    private string TriggerFile(string id, string name) { var path = Path.Combine(TriggerDir(id), name); RejectLinks(path); return path; }
    private void InitializeTriggers()
    {
        var root = ReviewDirectory("diagnostic-triggers"); if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root))
        {
            RejectLinks(dir); var path = Path.Combine(dir, "definition.json"); if (!File.Exists(path)) continue;
            var definition = Read<DiagnosticTriggerDefinition>(path); var runtime = new TriggerRuntime(definition); triggers.Add(definition.Id, runtime);
            var pending = TriggerFile(definition.Id, "pending.json");
            if (File.Exists(pending)) { runtime.Pending = Read<DiagnosticEpisode>(pending); FinishEpisode(runtime, "interrupted", "Service restarted before the post-trigger window completed."); }
        }
    }
    private TriggerRuntime Trigger(string id) => triggers.GetValueOrDefault(Id(id)) ?? throw new OperationException("not_found", "Diagnostic trigger not found.");
    private DiagnosticEpisodeSummary[] TriggerEpisodes(TriggerRuntime runtime) => Directory.GetFiles(TriggerDir(runtime.Definition.Id), "episode-*.json")
        .Select(f => { RejectLinks(f); return Read<DiagnosticEpisodeSummary>(f); }).OrderByDescending(e => e.TriggeredAt).ToArray();
    private DiagnosticTriggerStatus TriggerStatus(TriggerRuntime r) => new(Clone(r.Definition)!, r.State, r.Buffer.Count, TriggerEpisodes(r), r.Error);
    public DiagnosticTriggerStatus[] DiagnosticTriggers() { lock (watchLock) return triggers.Values.Select(TriggerStatus).ToArray(); }
    public async Task<DiagnosticTriggerStatus> SaveDiagnosticTriggerAsync(DiagnosticTriggerSaveRequest request, CancellationToken ct = default)
    {
        SavedReviewValidation.Name(request.Name);
        if (request.Condition is not null) request = request with { Condition = request.Condition with { Value = request.Condition.Value?.Clone() } };
        if (request.Condition is null || request.PreSamples is < 0 or > 256 || request.PostSamples is < 0 or > 256 || request.RetainEpisodes is < 1 or > 20 ||
            request.Condition.Kind is not ("fault" or "change" or "predicate" or "timeout")) throw new OperationException("diagnostic_trigger_settings", "Use a supported condition, 0..256 pre/post samples and 1..20 retained episodes.");
        WatchValidation.MemberPath(request.Condition.MemberPath);
        if (request.Condition.Kind is "predicate" or "timeout" && (request.Condition.Operator is not ("eq" or "ne" or "gt" or "ge" or "lt" or "le") || request.Condition.Value is null ||
            request.Condition.Value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String))) throw new OperationException("diagnostic_trigger_condition", "Choose a scalar comparison value and eq/ne/gt/ge/lt/le.");
        if (request.Condition.HoldMs is < 100 or > 86400000) throw new OperationException("diagnostic_trigger_condition", "Timeout hold must be 100..86400000 ms.");
        await gate.WaitAsync(ct);
        try
        {
            var p = Get(request.ProjectId); Expect(p, request.ExpectedSha256);
            lock (watchLock)
            {
                var watch = Watch(request.WatchId);
                if (watch.Definition.Revision != request.ExpectedWatchRevision || !watch.Definition.Settings.ExpectedSha256.Equals(p.CurrentSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Watch/project source revision changed.");
                if (request.Condition.Kind != "fault" && !watch.Definition.Settings.Tags.Any(t => t.Key == request.Condition.Key)) throw new OperationException("diagnostic_trigger_key", "Select a key in the exact watch definition.");
                TriggerRuntime runtime;
                if (request.Id is null)
                {
                    if (request.ExpectedRevision is not null || triggers.Count >= 100) throw new OperationException("diagnostic_trigger_limit", "Maximum 100 trigger definitions; new definitions have no expected revision.");
                    var id = Guid.NewGuid().ToString("N"); runtime = new(new(id, 0, request.Name.Trim(), p.Id, p.Revision, p.CurrentSha256, Clone(watch.Definition)!, request.Condition, request.PreSamples, request.PostSamples, request.RetainEpisodes));
                    Directory.CreateDirectory(TriggerDir(id)); triggers.Add(id, runtime);
                }
                else
                {
                    runtime = Trigger(request.Id);
                    if (runtime.State is "armed" or "capturing" || runtime.Definition.Revision != request.ExpectedRevision) throw new OperationException("stale_revision", "Disarm the current trigger and reload its revision before editing.");
                    runtime.Definition = new(runtime.Definition.Id, checked(runtime.Definition.Revision + 1), request.Name.Trim(), p.Id, p.Revision, p.CurrentSha256, Clone(watch.Definition)!, request.Condition, request.PreSamples, request.PostSamples, request.RetainEpisodes);
                    runtime.Buffer.Clear(); runtime.Previous = null; runtime.Since = null; runtime.State = "disarmed";
                }
                Save(TriggerFile(runtime.Definition.Id, "definition.json"), runtime.Definition); return TriggerStatus(runtime);
            }
        }
        finally { gate.Release(); }
    }
    public async Task<DiagnosticTriggerStatus> ArmDiagnosticTriggerAsync(string id, DiagnosticTriggerArmRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            lock (watchLock)
            {
                var r = Trigger(id); if (r.Definition.Revision != request.ExpectedRevision) throw new OperationException("stale_revision", "Trigger definition changed.");
                if (r.Pending is not null) FinishEpisode(r, "interrupted", "Explicitly disarmed before post window completed.");
                if (request.Armed)
                {
                    var p = Get(r.Definition.ProjectId); Expect(p, r.Definition.ProjectSha256); var w = Watch(r.Definition.Watch.Id);
                    if (w.Definition.Revision != r.Definition.Watch.Revision) throw new OperationException("stale_revision", "Watch definition changed; update the trigger binding.");
                }
                r.State = request.Armed ? "armed" : "disarmed"; r.Buffer.Clear(); r.BufferBytes = 0; r.Dropped = 0; r.Previous = null; r.Since = null; r.LastAt = null; r.Session = ""; r.Error = null;
                return TriggerStatus(r); // Arming never starts or reconnects a watch reader.
            }
        }
        finally { gate.Release(); }
    }
    private bool TriggerCondition(TriggerRuntime r, DiagnosticFrame frame)
    {
        var c = r.Definition.Condition;
        if (c.Kind == "fault") return frame.Kind == "fault";
        var sample = frame.Sample; var v = sample?.Values.SingleOrDefault(v => v.Key == c.Key);
        if (v?.Quality != "good") { r.Previous = null; r.Since = null; return false; }
        JsonNode? value;
        try { value = LogixStructures.Select(v.Value, c.MemberPath); } catch (OperationException) { value = null; }
        if (value is null || value is JsonObject or JsonArray) { r.Previous = null; r.Since = null; return false; }
        if (c.Kind == "change") { var changed = r.Previous is not null && !JsonNode.DeepEquals(r.Previous, value); r.Previous = value.DeepClone(); return changed; }
        var threshold = JsonNode.Parse(c.Value!.Value.GetRawText());
        bool? matches = c.Operator switch { "eq" => JsonNode.DeepEquals(value, threshold), "ne" => !JsonNode.DeepEquals(value, threshold), _ => null };
        if (matches is null && decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(threshold?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            matches = c.Operator switch { "gt" => a > b, "ge" => a >= b, "lt" => a < b, "le" => a <= b, _ => null };
        if (matches != true) { r.Since = null; return false; }
        r.Since ??= frame.At; return c.Kind != "timeout" || frame.At - r.Since >= TimeSpan.FromMilliseconds(c.HoldMs);
    }
    private void FeedDiagnosticTriggers(WatchRuntime watch, DiagnosticFrame frame)
    {
        foreach (var r in triggers.Values.Where(r => r.Definition.Watch.Id == watch.Definition.Id && r.State is "armed" or "capturing"))
        try
        {
            if (watch.Definition.Revision != r.Definition.Watch.Revision) { if (r.Pending is not null) FinishEpisode(r, "interrupted", "Watch revision changed."); r.State = "disarmed"; continue; }
            if (r.Session != watch.Session || r.LastAt is { } last && (frame.At <= last || frame.At - last > TimeSpan.FromMilliseconds(Math.Max(1000, watch.Definition.Settings.IntervalMs * 2L))))
            { r.Buffer.Clear(); r.BufferBytes = 0; r.Previous = null; r.Since = null; if (r.Pending is not null) r.Pending = r.Pending with { Diagnostics = r.Pending.Diagnostics.Append("Polling session/time gap observed inside the capture window.").ToArray() }; }
            r.Session = watch.Session; r.LastAt = frame.At;
            if (r.Pending is not null)
            {
                var next = r.Pending with { Frames = r.Pending.Frames.Append(frame).ToArray() };
                if (JsonSerializer.SerializeToUtf8Bytes(next, Json).Length > 8 * 1024 * 1024) FinishEpisode(r, "limited", "Post samples exceeded the 8 MiB episode budget.");
                else { r.Pending = next; Save(TriggerFile(r.Definition.Id, "pending.json"), next); if (frame.Sample is not null && --r.PostRemaining <= 0) FinishEpisode(r, "completed", null); }
            }
            else if (TriggerCondition(r, frame))
            {
                var frames = r.Buffer.Append(frame with { Trigger = true }).ToArray();
                r.Pending = new(Guid.NewGuid().ToString("N"), Clone(r.Definition)!, watch.Session, frame.At, "capturing", frames, r.Dropped, []); r.PostRemaining = r.Definition.PostSamples; r.State = "capturing";
                while (JsonSerializer.SerializeToUtf8Bytes(r.Pending, Json).Length > 8 * 1024 * 1024 && r.Pending.Frames.Length > 1)
                    r.Pending = r.Pending with { Frames = r.Pending.Frames[1..], DroppedPreSamples = r.Pending.DroppedPreSamples + 1 };
                Save(TriggerFile(r.Definition.Id, "pending.json"), r.Pending);
                if (r.PostRemaining == 0) FinishEpisode(r, "completed", null);
            }
            if (frame.Sample is not null && r.State == "armed")
            {
                r.Buffer.Enqueue(frame); r.BufferBytes += JsonSerializer.SerializeToUtf8Bytes(frame, Json).Length;
                while (r.Buffer.Count > r.Definition.PreSamples) r.BufferBytes -= JsonSerializer.SerializeToUtf8Bytes(r.Buffer.Dequeue(), Json).Length;
                while (r.BufferBytes > 4 * 1024 * 1024) { r.BufferBytes -= JsonSerializer.SerializeToUtf8Bytes(r.Buffer.Dequeue(), Json).Length; r.Dropped++; }
            }
            if (frame.Kind is "fault" or "stop") { if (r.Pending is not null) FinishEpisode(r, "interrupted", "Polling ended before the post-trigger window completed."); if (r.State == "armed") r.State = "disarmed"; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationException)
        { r.State = "faulted"; r.Error = ex.Message; }
    }
    private void FinishEpisode(TriggerRuntime r, string status, string? detail)
    {
        var episode = r.Pending! with { Status = status, Diagnostics = detail is null ? r.Pending!.Diagnostics : r.Pending!.Diagnostics.Append(detail).ToArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(episode, Json); var dir = ArtifactDir(episode.Id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project.json"), bytes);
        var artifact = new Artifact(episode.Id, episode.Definition.ProjectId, "json", Hash(bytes), bytes.Length, "Bounded diagnostic episode from authorized watch samples. Source/watch revision, trigger, gaps and interrupted windows are retained; no atomic controller snapshot is claimed.");
        Save(Path.Combine(dir, "artifact.json"), artifact); episode = episode with { Artifact = artifact };
        Save(TriggerFile(r.Definition.Id, "episode-" + episode.Id + ".json"), new DiagnosticEpisodeSummary(episode.Id, episode.TriggeredAt, episode.Status, episode.Frames.Length, artifact)); File.Delete(TriggerFile(r.Definition.Id, "pending.json")); r.Pending = null; r.State = "complete";
        foreach (var old in TriggerEpisodes(r).Skip(r.Definition.RetainEpisodes)) File.Delete(TriggerFile(r.Definition.Id, "episode-" + old.Id + ".json")); // Immutable artifacts remain separately available.
    }
    public DiagnosticEpisode GetDiagnosticEpisode(string triggerId, string episodeId)
    {
        lock (watchLock)
        {
            _ = Trigger(triggerId); var summary = Read<DiagnosticEpisodeSummary>(TriggerFile(triggerId, "episode-" + Id(episodeId) + ".json")); var (artifact, path) = Download(summary.Artifact.Id);
            if (artifact.Sha256 != summary.Artifact.Sha256) throw new OperationException("state_tampered", "Episode artifact hash mismatch.");
            var episode = Read<DiagnosticEpisode>(path);
            if (episode.Id != Id(episodeId) || episode.Definition.Id != Id(triggerId)) throw new OperationException("state_tampered", "Episode identity mismatch.");
            return episode with { Artifact = artifact };
        }
    }
    public object DeleteDiagnosticTrigger(string id, ReviewDeleteRequest request)
    {
        lock (watchLock)
        {
            var r = Trigger(id);
            if (r.Definition.Revision != request.ExpectedRevision || r.State is "armed" or "capturing") throw new OperationException("stale_revision", "Disarm the exact current trigger revision before removing its definition.");
            File.Delete(TriggerFile(id, "definition.json")); triggers.Remove(id); return new { deleted = true, artifactsPreserved = true };
        }
    }
    public async Task<DiagnosticCaptureInfo> CaptureDiagnosticEpisodeAsync(string triggerId, string episodeId, DiagnosticEpisodeCaptureRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var e = GetDiagnosticEpisode(triggerId, episodeId);
            if (!e.Artifact!.Sha256.Equals(request.ExpectedEpisodeSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Episode hash changed.");
            var frame = request.FrameIndex == -1 ? e.Frames.FirstOrDefault(f => f.Trigger && f.Sample is not null) : e.Frames.ElementAtOrDefault(request.FrameIndex);
            if (frame?.Sample is not { } sample) throw new OperationException("diagnostic_capture_sample", "Select a frame containing captured values; fault events may have no sample.");
            var d = e.Definition;
            return PublishDiagnosticCapture(new(d.ProjectId, d.ProjectRevision, d.ProjectSha256, sample.StartedAt, sample.CompletedAt, "trigger_episode", d.Watch.Id, d.Watch.Revision, null,
                d.Watch.Settings.Tags.Select(t => { var v = sample.Values.SingleOrDefault(v => v.Key == t.Key); return new DiagnosticCapturedValue(t, v?.Quality ?? "unavailable", Clone(v?.Value), v?.Error); }).ToArray(), LogicEvaluation.Boundary));
        }
        finally { gate.Release(); }
    }
}
