using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record VersionCompatibilityRequest(string ProjectId, string ExpectedSha256, int TargetRevision);
public sealed record VersionCompatibilityPlan(VersionCompatibilityRequest Request, ComparisonSnapshot Source, int SourceRevision, bool CanConvert,
    bool NativeAvailable, string ReviewSha256, SdkRunRequest NativeRequest, string[] Issues, string Boundary);
public sealed record VersionCompatibilityStartRequest(VersionCompatibilityRequest Preview, string ExpectedReviewSha256);
public sealed record VersionCompatibilityRun(string Id, DateTimeOffset CreatedAt, VersionCompatibilityPlan Plan, string ConversionJobId, string? ReadbackJobId = null);
public sealed record VersionReadbackReport(bool TargetRevisionMatches, int? SoftwareRevision, int? ControllerRevision, int ConfigurationChanges,
    ProjectDifference[] Differences, string[] UnsupportedOrDefaultedMessages, string Boundary);
public sealed record VersionCompatibilityStatus(VersionCompatibilityRun Run, SdkJob Conversion, SdkJob? Readback, VersionReadbackReport? Report);

public static class VersionCompatibility
{
    public const string Boundary = "Native offline compatibility workflow on separate private copies. SDK ConvertAsync supports upgrading projects, not generic downgrade/header rewriting. Conversion completion is distinct from reopening the saved ACD and inspecting a fresh L5X readback. XML configuration differences and native unsupported/default messages require review; native import/build/runtime acceptance remains version/project-specific.";
    private static int? SoftwareMajor(XDocument doc) => int.TryParse(((string?)doc.Root?.Attribute("SoftwareRevision"))?.Split('.')[0], out var revision) ? revision : null;
    private static int? ControllerMajor(XDocument doc) => int.TryParse((string?)doc.Root?.Element("Controller")?.Attribute("MajorRev"), out var revision) ? revision : null;
    internal static int? Major(XDocument doc) => SoftwareMajor(doc) ?? ControllerMajor(doc);
    public static VersionReadbackReport CompareReadback(ProjectInfo source, XDocument before, ProjectInfo result, XDocument after, int target, SdkEvent[] events)
    {
        var software = SoftwareMajor(after); var controller = ControllerMajor(after);
        var a = new XDocument(before); var b = new XDocument(after);
        foreach (var doc in new[] { a, b }) { doc.Root?.Attribute("SoftwareRevision")?.Remove(); doc.Root?.Element("Controller")?.Attribute("MajorRev")?.Remove(); }
        var report = ProjectComparison.Compare(source, a, result, b, new(source.Id, source.CurrentSha256, result.Id, result.CurrentSha256, Limit: 200));
        var messages = events.Where(e => e.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("default", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase)).Select(e => e.Message.Length > 2048 ? e.Message[..2048] : e.Message).Take(100).ToArray();
        return new(software == target && controller == target, software, controller, report.TotalChanges, report.Differences, messages,
            Boundary + " Expected software/controller major-version fields are excluded from configuration differences; added/removed/modified XML and any observed native diagnostics are retained. XML absence alone does not prove that an option was unsupported or defaulted.");
    }
}

public sealed partial class ProjectService
{
    private readonly SemaphoreSlim compatibilityGate = new(1, 1);
    public VersionCompatibilityPlan PreviewVersionCompatibility(VersionCompatibilityRequest request)
    {
        var p = Get(request.ProjectId); Expect(p, request.ExpectedSha256); if (p.Kind != "logix") throw new OperationException("not_logix", "Select a Logix snapshot.");
        var doc = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(p.Id, p.Revision))); var major = VersionCompatibility.Major(doc); var issues = new List<string>();
        if (request.TargetRevision is < 31 or > 38) issues.Add("target_revision_unsupported: supported workflow targets are 31..38.");
        if (major is null or < 31 or > 38) issues.Add("source_revision_unknown_or_unsupported: select a source with a known software/controller major revision 31..38.");
        if (int.TryParse((string?)doc.Root?.Element("Controller")?.Attribute("MajorRev"), out var controllerMajor) && major != controllerMajor)
            issues.Add("source_revision_inconsistent: software and controller major revisions differ; obtain a consistent native export before conversion.");
        if (major is not null && request.TargetRevision < major) issues.Add("downgrade_unsupported: SDK conversion upgrades projects; this workflow never rewrites version headers to claim downgrade compatibility.");
        static SdkCall Call(string method, object? args = null) => new(method, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args ?? new { }, Json))!);
        var calls = new List<SdkCall>();
        if (major != request.TargetRevision) calls.Add(Call("ConvertAsync", new { destinationRevision = request.TargetRevision }));
        if (request.TargetRevision >= 37) calls.Add(Call("BuildAsync"));
        calls.Add(Call("SaveAsync"));
        var proposed = new SdkRunRequest(calls.ToArray(), p.Id, p.CurrentSha256); var supported = issues.Count == 0;
        if (!native.Available) issues.Add("native_unavailable: build/configure the SDK bridge and install the intended Logix version before starting conversion.");
        var review = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, sourceRevision = p.Revision, proposed }, Json));
        return new(request, new(p.Id, p.Name, p.Revision, p.CurrentSha256), major ?? 0, supported, native.Available, review, proposed, issues.ToArray(), VersionCompatibility.Boundary);
    }
    public async Task<VersionCompatibilityRun> StartVersionCompatibilityAsync(VersionCompatibilityStartRequest request, CancellationToken ct = default)
    {
        var preview = PreviewVersionCompatibility(request.Preview);
        if (!preview.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Version conversion differs from the reviewed plan.");
        if (!preview.CanConvert) throw new OperationException("version_conversion_unsupported", string.Join("; ", preview.Issues));
        var job = await StartSdkJobAsync(preview.NativeRequest, ct); // Shared SDK layer snapshots inputs and forbids controller effects in this generated request.
        var run = new VersionCompatibilityRun(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, preview, job.Id);
        Directory.CreateDirectory(ReviewDirectory("version-compatibility")); Save(ReviewPath("version-compatibility", run.Id), run); return run;
    }
    public async Task<VersionCompatibilityRun> StartVersionReadbackAsync(string id, CancellationToken ct = default)
    {
        await compatibilityGate.WaitAsync(ct);
        try
        {
            var run = Read<VersionCompatibilityRun>(ExistingReviewPath("version-compatibility", id)); if (run.ReadbackJobId is not null) return run;
            var conversion = SdkJob(run.ConversionJobId);
            if (conversion.Status != "succeeded" || conversion.ControllerEffectsPossible) throw new OperationException("version_conversion_pending", "Wait for a successful offline conversion job before requesting ACD readback.");
            var acd = conversion.Artifacts.SingleOrDefault(a => a.Format == "acd" && a.Validation.Contains("output final.acd;", StringComparison.Ordinal)) ?? throw new OperationException("version_conversion_output", "Converted native ACD artifact is unavailable.");
            _ = Download(acd.Id);
            var job = await StartSdkJobAsync(new([new("SaveAsync", [])], SourceArtifactId: acd.Id), ct);
            run = run with { ReadbackJobId = job.Id }; Save(ReviewPath("version-compatibility", id), run); return run;
        }
        finally { compatibilityGate.Release(); }
    }
    public VersionCompatibilityStatus VersionCompatibilityStatus(string id)
    {
        var run = Read<VersionCompatibilityRun>(ExistingReviewPath("version-compatibility", id)); var conversion = SdkJob(run.ConversionJobId); var readback = run.ReadbackJobId is null ? null : SdkJob(run.ReadbackJobId);
        VersionReadbackReport? report = null;
        if (readback is { Status: "succeeded", ResultProject: { } result })
        {
            var source = Get(run.Plan.Source.Id) with { Revision = run.Plan.Source.Revision, CurrentSha256 = run.Plan.Source.Sha256 };
            var before = File.ReadAllBytes(RevisionFile(source.Id, source.Revision)); var after = File.ReadAllBytes(RevisionFile(result.Id, result.Revision));
            if (Hash(before) != source.CurrentSha256 || Hash(after) != result.CurrentSha256) throw new OperationException("state_tampered", "Source/readback project hash changed.");
            if (readback.ControllerEffectsPossible || readback.SourceSha256 != conversion.Artifacts.Single(a => a.Format == "acd" && a.Validation.Contains("output final.acd;", StringComparison.Ordinal)).Sha256)
                throw new OperationException("state_tampered", "Readback is not bound to the converted offline ACD artifact.");
            report = VersionCompatibility.CompareReadback(source, XmlProjects.Parse(before), result, XmlProjects.Parse(after), run.Plan.Request.TargetRevision, conversion.Events.Concat(readback.Events).ToArray());
        }
        return new(run, conversion, readback, report);
    }
}
