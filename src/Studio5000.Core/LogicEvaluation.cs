using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record DiagnosticCaptureRequest(string ProjectId, string ExpectedSha256, WatchTag[]? Tags = null, string? WatchId = null, int? WatchRevision = null, long? RecordIndex = null);
public sealed record DiagnosticCapturedValue(WatchTag Tag, string Quality, JsonNode? Value, string? Error = null);
public sealed record DiagnosticCaptureDocument(string ProjectId, int ProjectRevision, string ProjectSha256, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    string Origin, string? WatchId, int? WatchRevision, long? RecordIndex, DiagnosticCapturedValue[] Values, string Boundary);
public sealed record DiagnosticCaptureInfo(string Id, string ProjectId, int ProjectRevision, string ProjectSha256, DateTimeOffset At, string Origin, Artifact Artifact);
public sealed record RungEvaluationRequest(string CaptureId, string ExpectedCaptureSha256, string RoutineXPath, int RungIndex = 0);
public sealed record LogicEvaluationStep(string Id, string Instruction, string[] Operands, string Condition, string PowerIn, string PowerOut, string Reason);
public sealed record LogicEvaluatedOutput(string Operand, string Instruction, string Power, string[] BlockingConditions, string[] UnknownConditions);
public sealed record RungEvaluationReport(DiagnosticCaptureInfo Capture, string RoutineXPath, int RungIndex, string Source, string Power,
    LogicEvaluationStep[] Steps, LogicEvaluatedOutput[] Outputs, string[] Diagnostics, string Boundary);

public static class LogicEvaluation
{
    public const string Boundary = "Conditional explanation using an immutable captured-value set and its exact saved project revision. Supports static XIC/XIO, numeric EQU/NEQ/GRT/GEQ/LES/LEQ/LIM, AFI/NOP and OTE power conditions with AND/OR branches. Stateful/unsupported instructions, unavailable values, indirect addresses and values affected by earlier writes remain unknown. No scan scheduling, I/O state, force state, simultaneous sampling or native execution is established. Captured values are never modified.";
    private sealed record State(string Truth, string[] Blockers, string[] Unknown)
    {
        internal static State Known(bool value, string? id = null) => new(value ? "true" : "false", !value && id is not null ? [id] : [], []);
        internal static State UnknownAt(string id) => new("unknown", [], [id]);
        internal static State And(State a, State b) => new(a.Truth == "false" || b.Truth == "false" ? "false" : a.Truth == "unknown" || b.Truth == "unknown" ? "unknown" : "true",
            a.Blockers.Concat(b.Blockers).Distinct().ToArray(), a.Unknown.Concat(b.Unknown).Distinct().ToArray());
        internal static State Or(State a, State b) => a.Truth == "true" || b.Truth == "true" ? Known(true) :
            new(a.Truth == "unknown" || b.Truth == "unknown" ? "unknown" : "false", a.Blockers.Concat(b.Blockers).Distinct().ToArray(), a.Unknown.Concat(b.Unknown).Distinct().ToArray());
    }
    public static RungEvaluationReport Evaluate(DiagnosticCaptureInfo capture, DiagnosticCaptureDocument values, XDocument document, RungEvaluationRequest request)
    {
        if (request.RungIndex is < 0 or > 100000 || request.RoutineXPath.Length > 8192) throw new OperationException("logic_evaluation_query", "Use an exact RLL routine and a nonnegative rung index.");
        var routine = XmlProjects.Select(document, request.RoutineXPath);
        if (routine.Name != "Routine" || (string?)routine.Attribute("Type") != "RLL") throw new OperationException("not_ladder", "Select an RLL routine.");
        var rung = routine.Elements("RLLContent").Elements("Rung").ElementAtOrDefault(request.RungIndex) ?? throw new OperationException("logic_evaluation_rung", "Rung index is outside this saved routine.");
        var source = (string?)rung.Element("Text") ?? ""; var context = new LogicContext(document); var program = (string?)routine.Ancestors("Program").FirstOrDefault()?.Attribute("Name");
        if (routine.Ancestors("AddOnInstructionDefinition").Any()) throw new OperationException("logic_evaluation_context", "Select a program RLL routine; AOI instance execution is not inferred.");
        var steps = new List<LogicEvaluationStep>(); var outputs = new List<LogicEvaluatedOutput>(); var diagnostics = new HashSet<string>();
        var invalidated = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var unknownEffects = false;
        JsonNode? Value(string operand, out string? issue)
        {
            issue = null;
            if (decimal.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number);
            if (bool.TryParse(operand, out var boolean)) return JsonValue.Create(boolean);
            var address = context.Resolve(operand, program);
            if (address is null || address.Issue is not null) { issue = address?.Issue ?? "unsupported_operand"; return null; }
            if (unknownEffects || invalidated.Contains(address.Symbol)) { issue = "earlier_instruction_may_have_written_value"; return null; }
            foreach (var v in values.Values)
            {
                var selected = context.Resolve((v.Tag.ProgramName is null ? "" : "Program:" + v.Tag.ProgramName + ".") + v.Tag.TagName, null);
                if (selected is null || !LogicContext.Same(selected.Symbol, address.Symbol)) continue;
                var selectedMember = selected.Member + (string.IsNullOrEmpty(v.Tag.MemberPath) ? "" : v.Tag.MemberPath.StartsWith('[') ? v.Tag.MemberPath : "." + v.Tag.MemberPath);
                if (!address.Member.StartsWith(selectedMember, StringComparison.OrdinalIgnoreCase) || address.Member.Length > selectedMember.Length && selectedMember.Length != 0 && address.Member[selectedMember.Length] is not ('.' or '[')) continue;
                if (v.Quality is not ("good" or "snapshot")) { issue = "captured_quality_" + v.Quality; return null; }
                var suffix = address.Member[selectedMember.Length..].TrimStart('.');
                try
                {
                    var dot = suffix.LastIndexOf('.'); var bitText = dot < 0 ? suffix : suffix[(dot + 1)..];
                    if (int.TryParse(bitText, out var bit) && bit is >= 0 and < 64)
                    {
                        var parent = dot < 0 ? v.Value : LogixStructures.Select(v.Value, suffix[..dot]);
                        if (BigInteger.TryParse(parent?.ToJsonString(), CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create((integer & (BigInteger.One << bit)) != 0);
                    }
                    return suffix.Length == 0 ? v.Value : LogixStructures.Select(v.Value, suffix);
                }
                catch (Exception ex) when (ex is OperationException or InvalidOperationException) { issue = "captured_member_unavailable"; return null; }
            }
            issue = "value_not_captured"; return null;
        }
        State Run(List<LadderPreview.Part> parts, State input, string prefix)
        {
            var power = input;
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i]; var id = prefix + "." + i; var before = power;
                if (p.Legs is { } legs)
                {
                    var branches = legs.Select((leg, n) => Run(leg, before, id + "/branch" + n)).ToArray();
                    power = branches.Aggregate(State.Known(false), State.Or);
                    steps.Add(new(id, "BRANCH", [], power.Truth, before.Truth, power.Truth, "Parallel legs join with OR; each leg retains series AND conditions.")); continue;
                }
                var name = p.Instruction.ToUpperInvariant(); var condition = State.Known(true); var reason = "Passes the current rung condition.";
                if (name is "XIC" or "XIO" or "EQU" or "NEQ" or "GRT" or "GEQ" or "LES" or "LEQ" or "LIM")
                {
                    var expected = name is "XIC" or "XIO" ? 1 : name == "LIM" ? 3 : 2;
                    var operands = p.Operands.Select(o => { var value = Value(o, out var issue); return (Value: value, Issue: issue); }).ToArray();
                    if (operands.Length != expected || operands.Any(v => v.Value is null || v.Issue is not null))
                    { condition = State.UnknownAt(id); reason = string.Join("; ", operands.Select(v => v.Issue).Where(v => v is not null)); if (reason.Length == 0) reason = "Unsupported operand count/value."; }
                    else
                    {
                        bool? result = null;
                        if (name is "XIC" or "XIO")
                        {
                            var scalar = operands[0].Value!.ToJsonString(); var bit = scalar is "true" or "1" ? true : scalar is "false" or "0" ? false : (bool?)null;
                            result = name == "XIC" ? bit : !bit;
                        }
                        else if (operands.All(v => decimal.TryParse(v.Value!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                        {
                            var n = operands.Select(v => decimal.Parse(v.Value!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                            result = name switch { "EQU" => n[0] == n[1], "NEQ" => n[0] != n[1], "GRT" => n[0] > n[1], "GEQ" => n[0] >= n[1], "LES" => n[0] < n[1], "LEQ" => n[0] <= n[1], "LIM" => n[0] <= n[2] ? n[1] >= n[0] && n[1] <= n[2] : n[1] >= n[0] || n[1] <= n[2], _ => null };
                        }
                        condition = result is null ? State.UnknownAt(id) : State.Known(result.Value, id); reason = result is null ? "Captured scalar type is unsupported for this predicate." : "Predicate evaluated against the captured values.";
                    }
                }
                else if (name == "AFI") { condition = State.Known(false, id); reason = "Always-false instruction blocks this series path."; }
                else if (name == "OTE" && p.Operands.Length == 1)
                {
                    outputs.Add(new(p.Operands[0], name, power.Truth, power.Truth == "false" ? power.Blockers : [], power.Unknown));
                    var target = context.Resolve(p.Operands[0], program); if (target is not null) invalidated.Add(target.Symbol);
                    reason = "OTE would use this rung condition; captured output data is not changed.";
                }
                else if (name != "NOP")
                {
                    condition = State.UnknownAt(id); reason = "Stateful or unsupported instruction; no execution/state transition is inferred."; diagnostics.Add("unsupported_instruction: " + p.Instruction);
                    if (power.Truth != "false")
                    {
                        var roles = LogicTracing.Roles(name, p.Operands.Length);
                        if (roles.Any(r => r == "unknown")) unknownEffects = true;
                        for (var n = 0; n < roles.Length; n++) if (roles[n] is "write" or "read-write" && context.Resolve(p.Operands[n], program) is { } a) invalidated.Add(a.Symbol);
                    }
                }
                if (condition.Truth == "unknown") diagnostics.Add(id + ": " + reason);
                power = State.And(power, condition); steps.Add(new(id, p.Instruction, p.Operands, condition.Truth, before.Truth, power.Truth, reason));
            }
            return power;
        }
        State result;
        try { result = Run(LadderPreview.ParseLogic(source), State.Known(true), "rung"); }
        catch (OperationException ex) { result = State.UnknownAt("parse"); diagnostics.Add(ex.Code + ": " + ex.Message); outputs.Clear(); steps.Clear(); }
        return new(capture, request.RoutineXPath, request.RungIndex, source, result.Truth, steps.ToArray(), outputs.ToArray(), diagnostics.ToArray(), Boundary);
    }
}

public sealed partial class ProjectService
{
    public DiagnosticCaptureInfo[] DiagnosticCaptures() => Directory.Exists(ReviewDirectory("diagnostic-captures")) ?
        Directory.GetFiles(ReviewDirectory("diagnostic-captures"), "*.json").Select(f => { RejectLinks(f); return Read<DiagnosticCaptureInfo>(f); }).OrderByDescending(c => c.At).Take(100).ToArray() : [];
    public async Task<DiagnosticCaptureInfo> CaptureDiagnosticValuesAsync(DiagnosticCaptureRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var p = Get(request.ProjectId); Expect(p, request.ExpectedSha256); if (p.Kind != "logix") throw new OperationException("not_logix", "Select a Logix snapshot.");
            DiagnosticCaptureDocument document;
            if (request.WatchId is { } watchId)
            {
                lock (watchLock)
                {
                    var watch = Watch(watchId); var revision = request.WatchRevision ?? watch.Definition.Revision; var def = watch.Recorder.Definition(revision);
                    if (!def.Settings.ExpectedSha256.Equals(p.CurrentSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Watch source revision differs from the selected project.");
                    var sample = request.RecordIndex is { } index ? watch.Recorder.Query(new(revision, AfterIndex: Math.Max(0, index - 1)), revision).FirstOrDefault(r => r.Index == index)?.Snapshot : revision == watch.Definition.Revision ? watch.Latest : null;
                    if (sample is null) throw new OperationException("diagnostic_capture_sample", "Select an available recorded sample or current latest sample.");
                    document = new(p.Id, p.Revision, p.CurrentSha256, sample.StartedAt, sample.CompletedAt, "watch_sample", watchId, revision, request.RecordIndex,
                        def.Settings.Tags.Select(t => { var v = sample.Values.SingleOrDefault(v => v.Key == t.Key); return new DiagnosticCapturedValue(t, v?.Quality ?? "unavailable", Clone(v?.Value), v?.Error); }).ToArray(), LogicEvaluation.Boundary);
                }
            }
            else
            {
                if (request.Tags is null || request.Tags.Length is < 1 or > 64) throw new OperationException("diagnostic_capture_tags", "Select 1..64 saved base-tag/member values or a watch sample.");
                var doc = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(p.Id, p.Revision))); var at = DateTimeOffset.UtcNow;
                var values = request.Tags.Select(t =>
                {
                    _ = WatchValidation.TagPath(t); WatchValidation.MemberPath(t.MemberPath);
                    var value = LogixStructures.ExtractTag(LogixStructures.Tag(doc, t.TagName, t.ProgramName), t.ProgramName);
                    return new DiagnosticCapturedValue(t, value.Quality, value.Quality == "snapshot" ? LogixStructures.Select(value.Value, t.MemberPath) : null, value.Error);
                }).ToArray();
                document = new(p.Id, p.Revision, p.CurrentSha256, at, at, "saved_values", null, null, null, values, LogicEvaluation.Boundary);
            }
            return PublishDiagnosticCapture(document);
        }
        finally { gate.Release(); }
    }
    private DiagnosticCaptureInfo PublishDiagnosticCapture(DiagnosticCaptureDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > 4 * 1024 * 1024) throw new OperationException("diagnostic_capture_limit", "Captured values exceed 4 MiB.");
        var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project.json"), bytes);
        var artifact = new Artifact(id, document.ProjectId, "json", Hash(bytes), bytes.Length, LogicEvaluation.Boundary); Save(Path.Combine(dir, "artifact.json"), artifact);
        var info = new DiagnosticCaptureInfo(id, document.ProjectId, document.ProjectRevision, document.ProjectSha256, document.CompletedAt, document.Origin, artifact);
        Directory.CreateDirectory(ReviewDirectory("diagnostic-captures")); Save(ReviewPath("diagnostic-captures", id), info); return info;
    }
    public RungEvaluationReport EvaluateCapturedRung(RungEvaluationRequest request)
    {
        var capture = Read<DiagnosticCaptureInfo>(ExistingReviewPath("diagnostic-captures", request.CaptureId)); var (artifact, path) = Download(capture.Artifact.Id);
        if (!artifact.Sha256.Equals(request.ExpectedCaptureSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Captured value hash differs from the requested capture.");
        var values = Read<DiagnosticCaptureDocument>(path); var file = RevisionFile(capture.ProjectId, capture.ProjectRevision); RejectLinks(file); var bytes = File.ReadAllBytes(file);
        if (Hash(bytes) != capture.ProjectSha256 || values.ProjectSha256 != capture.ProjectSha256) throw new OperationException("state_tampered", "Saved capture/project revision hash mismatch.");
        return LogicEvaluation.Evaluate(capture, values, XmlProjects.Parse(bytes), request);
    }
}
