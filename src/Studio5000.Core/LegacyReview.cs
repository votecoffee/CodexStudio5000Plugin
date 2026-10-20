using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record LegacyReviewRequest(string ExportJobId, string L5kArtifactId, string ExpectedSha256, string Purpose,
    string Profile = "v35-to-v32-review");
public sealed record LegacyReviewEdit(string Location, int Start, int Length, string Before, string After, string Reason);
public sealed record LegacyReviewPlan(LegacyReviewRequest Request, ComparisonSnapshot NativeSource, string ExportSourceSha256,
    Artifact NativeExport, string ControllerName, int TargetRevision, string PreparedSha256, int PreparedBytes,
    LegacyReviewEdit[] Edits, string ReviewSha256, bool ReviewOnly, string[] RequiredChecks, string Boundary,
    string EditOffsetUnit = "Zero-based UTF-16 character offsets in decoded text, excluding BOM");
public sealed record LegacyReviewPrepareRequest(LegacyReviewRequest Preview, string ExpectedReviewSha256);
public sealed record LegacyReviewPrepared(Artifact Input, Artifact Manifest, string ReviewSha256, SdkRunRequest ImportRequest,
    string NativeAcceptance, string Boundary);
public sealed record LegacyReviewManifest(int SchemaVersion, string Kind, string ToolVersion, DateTimeOffset CreatedAt, LegacyReviewPlan Plan);

// This bounded lexical editor is deliberately separate from SDK version conversion.
// It never edits code, scans errors to remove unsupported settings, or claims a native downgrade.
internal static class LegacyReview
{
    public const string Boundary = "REVIEW ONLY: prepare a separate v35 native L5K export for an attempted v32 native import. " +
        "Only IE_VER 2.26 to 2.23, controller/Local major 35 to 32 and explicit AutoDiagsEnabled/WebServerEnabled omissions are supported. " +
        "Unselected UTF-8 bytes, including original exporter comments, are preserved. This is not a deployable downgrade or native acceptance. " +
        "Target installation/licensing, actual native import/save, saved-ACD reopen/readback and exact logic/value/configuration review remain required. " +
        "Motion defaults, unsupported instructions/hardware and safety/protected-code semantics are unresolved. No native job or controller connection is started.";
    internal const int MaximumBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Token(string Value, int Start, int End, bool Word = false);
    private sealed record Field(string Name, Token[] Value, int Start, int End, int? CommaBefore);
    private sealed class Lexer(string text)
    {
        private int at, count;
        public Token Next()
        {
            while (at < text.Length)
            {
                if (char.IsWhiteSpace(text[at])) { at++; continue; }
                if (text.AsSpan(at).StartsWith("(*", StringComparison.Ordinal))
                {
                    var depth = 1; at += 2;
                    while (at < text.Length && depth > 0)
                    {
                        if (text.AsSpan(at).StartsWith("(*", StringComparison.Ordinal)) { if (++depth > 64) throw Unsupported("Comment nesting exceeds 64."); at += 2; }
                        else if (text.AsSpan(at).StartsWith("*)", StringComparison.Ordinal)) { depth--; at += 2; }
                        else at++;
                    }
                    if (depth != 0) throw Unsupported("Unterminated L5K comment.");
                    continue;
                }
                break;
            }
            if (++count > 10000000) throw Unsupported("L5K exceeds the 10000000-token budget.");
            var start = at;
            if (at == text.Length) return new("", at, at);
            var c = text[at++];
            if (c is '\'' or '"')
            {
                while (at < text.Length)
                {
                    var next = text[at++];
                    if (next == '$') { if (at == text.Length) break; at++; }
                    else if (next == c) return new(text[start..at], start, at);
                }
                throw Unsupported("Unterminated L5K quoted value.");
            }
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] is '_' or '.' or '#')) at++;
                return new(text[start..at], start, at, true);
            }
            if (c == ':' && at < text.Length && text[at] == '=') at++;
            return new(text[start..at], start, at);
        }
    }
    private static OperationException Unsupported(string detail) => new("legacy_review_unsupported", detail);
    private static void Require(Token token, string expected)
    { if (token.Value != expected) throw Unsupported("Expected an unambiguous native L5K " + expected + " token."); }
    private static Field[] Header(Lexer lexer)
    {
        Require(lexer.Next(), "("); var result = new List<Field>(); int? comma = null; var length = 0;
        while (true)
        {
            var name = lexer.Next(); if (name.Value == ")" && result.Count == 0) return [];
            if (!name.Word || !char.IsAsciiLetter(name.Value[0]) || result.Any(f => f.Name.Equals(name.Value, StringComparison.OrdinalIgnoreCase)))
                throw Unsupported("Duplicate or unknown declaration field syntax.");
            Require(lexer.Next(), ":="); var values = new List<Token>(); var stack = new Stack<string>(); Token end;
            while (true)
            {
                end = lexer.Next(); if (++length > 10000 || end.Value.Length == 0 || end.End - name.Start > 256 * 1024) throw Unsupported("Declaration exceeds its bounded field budget.");
                if (stack.Count == 0 && end.Value is "," or ")") break;
                if (end.Value is "(" or "[") stack.Push(end.Value);
                if (end.Value is ")" or "]")
                { if (stack.Count == 0 || stack.Pop() != (end.Value == ")" ? "(" : "[")) throw Unsupported("Unbalanced declaration value."); }
                values.Add(end);
            }
            if (values.Count == 0) throw Unsupported("Empty declaration value.");
            result.Add(new(name.Value, values.ToArray(), name.Start, values[^1].End, comma));
            if (end.Value == ")") return result.ToArray();
            comma = end.Start;
        }
    }
    internal static (byte[] Bytes, LegacyReviewEdit[] Edits) Prepare(byte[] bytes, string controllerName)
    {
        if (bytes.Length > MaximumBytes) throw Unsupported("L5K exceeds 64 MiB.");
        var bom = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }); string text;
        try { text = Utf8.GetString(bytes.AsSpan(bom ? 3 : 0)); }
        catch (DecoderFallbackException) { throw Unsupported("Only strict UTF-8 native L5K is supported."); }
        if (text.Contains('\0')) throw Unsupported("NUL/UTF-16 inputs are unsupported.");
        var lexer = new Lexer(text); var edits = new List<LegacyReviewEdit>(); var controllers = 0; var locals = 0; var versions = 0; var endings = 0;
        bool LineStart(Token t) { for (var i = t.Start - 1; i >= 0 && text[i] is not ('\r' or '\n'); i--) if (!char.IsWhiteSpace(text[i])) return false; return true; }
        void Replace(string location, Token token, string expected, string value)
        {
            if (token.Value != expected) throw Unsupported(location + " must be exactly " + expected + ".");
            edits.Add(new(location, token.Start, token.End - token.Start, token.Value, value, "Explicit v35-to-v32 review-input field; native acceptance pending."));
        }
        void Major(Field[] fields, string location)
        {
            var major = fields.SingleOrDefault(f => f.Name == "Major");
            if (major?.Value.Length != 1) throw Unsupported(location + " requires one scalar Major field.");
            Replace(location + "/Major", major.Value[0], "35", "32");
        }
        while (true)
        {
            var token = lexer.Next(); if (token.Value.Length == 0) break;
            if (endings != 0 && token.Value != ";") throw Unsupported("Unexpected tokens after END_CONTROLLER.");
            if (!token.Word || !LineStart(token)) continue;
            if (token.Value == "IE_VER")
            {
                if (++versions != 1 || controllers != 0) throw Unsupported("IE_VER must occur once before the controller.");
                Require(lexer.Next(), ":="); Replace("IE_VER", lexer.Next(), "2.26", "2.23"); Require(lexer.Next(), ";");
            }
            else if (token.Value == "CONTROLLER")
            {
                if (++controllers != 1 || versions != 1) throw Unsupported("Expected one native controller after IE_VER.");
                Require(lexer.Next(), controllerName); var fields = Header(lexer); Major(fields, "CONTROLLER/" + controllerName);
                var omit = fields.Where(f => f.Name is "AutoDiagsEnabled" or "WebServerEnabled").ToArray();
                foreach (var field in omit) if (field.Value.Length != 1 || field.Value[0].Value is not ("0" or "1")) throw Unsupported("Unknown omitted field encoding: " + field.Name);
                var firstRetained = Array.FindIndex(fields, f => !omit.Contains(f));
                if (firstRetained > 0)
                {
                    var start = fields[0].Start; var end = fields[firstRetained].Start;
                    edits.Add(new("CONTROLLER/" + controllerName + "/" + string.Join("+", fields.Take(firstRetained).Select(f => f.Name)), start, end - start, text[start..end], "", "Explicit review-only omission of unsupported controller properties."));
                }
                foreach (var field in omit.Where(f => f.Start > fields[firstRetained].Start))
                {
                    var start = field.CommaBefore!.Value;
                    edits.Add(new("CONTROLLER/" + controllerName + "/" + field.Name, start, field.End - start, text[start..field.End], "", "Explicit review-only omission; no other settings are removed."));
                }
            }
            else if (token.Value == "MODULE")
            {
                var name = lexer.Next(); if (name.Value != "Local") continue;
                if (controllers != 1 || ++locals != 1) throw Unsupported("Expected one Local module within the controller.");
                Major(Header(lexer), "MODULE/Local");
            }
            else if (token.Value == "END_CONTROLLER")
            { if (controllers != 1 || ++endings != 1) throw Unsupported("Ambiguous controller ending."); }
        }
        if (versions != 1 || controllers != 1 || locals != 1 || endings != 1) throw Unsupported("Expected unique IE_VER, controller, Local module and controller ending.");
        var ordered = edits.OrderBy(e => e.Start).ToArray(); var builder = new StringBuilder(text.Length); var previous = 0;
        foreach (var edit in ordered)
        {
            if (edit.Start < previous) throw Unsupported("Overlapping review field edits.");
            builder.Append(text, previous, edit.Start - previous); builder.Append(edit.After); previous = edit.Start + edit.Length;
        }
        builder.Append(text, previous, text.Length - previous); var payload = Utf8.GetBytes(builder.ToString());
        return (bom ? new byte[] { 239, 187, 191 }.Concat(payload).ToArray() : payload, ordered);
    }
}

public sealed partial class ProjectService
{
    private (LegacyReviewPlan Plan, byte[] Bytes) BuildLegacyReview(LegacyReviewRequest request)
    {
        if (request.Profile != "v35-to-v32-review" || string.IsNullOrWhiteSpace(request.Purpose) || request.Purpose.Length > 500)
            throw new OperationException("legacy_review_policy", "Choose v35-to-v32-review and a nonempty review purpose up to 500 characters.");
        var job = SdkJob(request.ExportJobId);
        if (job.Status != "succeeded" || job.ControllerEffectsPossible || job.SourceSha256 is null || !job.Methods.Contains("SaveAsAsync") || job.ResultProject is null)
            throw new OperationException("legacy_review_provenance", "Select a completed offline SDK L5K export job with a retained native source snapshot.");
        var recorded = job.Artifacts.SingleOrDefault(a => a.Id == request.L5kArtifactId && a.Format == "l5k");
        if (recorded is null || recorded.ProjectId != job.ResultProject.Id || recorded.Bytes > LegacyReview.MaximumBytes || !recorded.Sha256.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "Select the exact L5K artifact/hash produced by the completed export job.");
        var (artifact, file) = Download(recorded.Id);
        if (artifact != recorded) throw new OperationException("artifact_tampered", "L5K artifact metadata differs from its export job.");
        var source = PreservationSource(new(job.ResultProject.Id, job.ResultProject.CurrentSha256)); var xml = XmlProjects.Parse(source.Bytes); LogixLexicalDocument.ValidateBudget(xml);
        if ((string?)xml.Root?.Attribute("SoftwareRevision") is not { } software || software.Split('.')[0] != "35" || (string?)xml.Root.Element("Controller")?.Attribute("MajorRev") != "35")
            throw new OperationException("legacy_review_unsupported", "This review profile requires a consistent native v35 L5X source snapshot.");
        var name = (string?)xml.Root!.Element("Controller")!.Attribute("Name") ?? throw new OperationException("legacy_review_unsupported", "Controller identity is missing.");
        var prepared = LegacyReview.Prepare(File.ReadAllBytes(file), name);
        var snapshot = new ComparisonSnapshot(source.Project.Id, source.Project.Name, source.Project.Revision, source.Project.CurrentSha256);
        var outputHash = Hash(prepared.Bytes);
        var review = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, snapshot, job.SourceSha256, artifact, outputHash, prepared.Edits, toolVersion = WorkspaceIdentity.Version }, Json));
        var checks = new[] { "Native v32 import/save and separate saved-ACD reopen/readback", "Target software/controller/Local revision from native readback", "All application/readable AOI routines and ordinary tag layouts/stored values", "Motion/defaulted configuration differences with native diagnostics", "Protected AOI envelope versus complete opaque payload; no payload semantic claim", "Retained legacy-review-readback validation receipt; review-only delivery" };
        return (new(request, snapshot, job.SourceSha256, artifact, name, 32, outputHash, prepared.Bytes.Length, prepared.Edits, review, true, checks, LegacyReview.Boundary), prepared.Bytes);
    }
    public async Task<LegacyReviewPlan> PreviewLegacyReviewAsync(LegacyReviewRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return BuildLegacyReview(request).Plan; } finally { gate.Release(); } }
    public async Task<LegacyReviewPrepared> PrepareLegacyReviewAsync(LegacyReviewPrepareRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var built = BuildLegacyReview(request.Preview);
            if (!built.Plan.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Review input, purpose, field edits or source changed; preview again.");
            Artifact Retain(byte[] bytes, string format)
            {
                var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project." + format), bytes);
                var artifact = new Artifact(id, built.Plan.NativeSource.Id, format, Hash(bytes), bytes.Length, LegacyReview.Boundary); Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
            }
            var input = Retain(built.Bytes, "l5k"); var manifest = Retain(JsonSerializer.SerializeToUtf8Bytes(new LegacyReviewManifest(1, "legacy_review_input", WorkspaceIdentity.Version, DateTimeOffset.UtcNow, built.Plan), Json), "json");
            var import = new SdkRunRequest([new("SaveAsync", [])], ExpectedSha256: input.Sha256, SourceArtifactId: input.Id);
            Audit("legacy_review_prepare", Get(built.Plan.NativeSource.Id), built.Plan.NativeExport.Sha256, input.Sha256, built.Plan.ReviewSha256);
            return new(input, manifest, built.Plan.ReviewSha256, import, "pending: preparation does not perform native import or validation", LegacyReview.Boundary);
        }
        finally { gate.Release(); }
    }
}
