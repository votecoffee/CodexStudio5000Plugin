using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio5000.Core;

public sealed record WatchTemplate(int SchemaVersion, string Name, WatchTag[] Tags, int IntervalMs = 1000, int HistoryLimit = 100, string Backend = "Sdk");
public sealed record WatchTemplateExportRequest(int ExpectedRevision);
public sealed record WatchTemplateProgramMap(string? SourceProgram, string? DestinationProgram);
public sealed record WatchTemplatePreviewRequest(JsonElement Template, string ProjectId, string ExpectedSha256, string? Name = null,
    WatchTemplateProgramMap[]? ProgramMappings = null, string Mode = "Offline", string? ControllerTargetId = null, string? Backend = null);
public sealed record WatchTemplatePreviewReport(string TemplateSha256, string ReviewSha256, BrowserWatchRequest Proposed,
    bool CanImport, WatchPreflightIssue[] ImportBlockers, WatchPreflightReport Preflight, string Coverage);
public sealed record WatchTemplateImportRequest(WatchTemplatePreviewRequest Preview, string ExpectedReviewSha256);

public static class WatchTemplates
{
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Strict = new(ProjectService.Json)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    public static WatchTemplate Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new OperationException("watch_template_invalid", "Expected a watch template JSON object.");
        var raw = json.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaximumBytes) throw new OperationException("watch_template_limit", "Template exceeds 256 KiB.");
        try
        {
            CheckKeys(json, 0);
            var template = JsonSerializer.Deserialize<WatchTemplate>(raw, Strict) ?? throw new JsonException("Empty template.");
            Validate(template); return template;
        }
        catch (JsonException ex) { throw new OperationException("watch_template_invalid", "Invalid template fields or JSON: " + ex.Message); }
    }
    public static void Validate(WatchTemplate template)
    {
        if (template.SchemaVersion != SchemaVersion) throw new OperationException("watch_template_version", "Only watch template schemaVersion 1 is supported.");
        WatchValidation.ValidatePortableSettings(template.Name, template.Tags, template.IntervalMs, template.HistoryLimit, template.Backend);
        foreach (var tag in template.Tags) { _ = WatchValidation.TagPath(tag); WatchValidation.MemberPath(tag.MemberPath); }
        if (JsonSerializer.SerializeToUtf8Bytes(template, ProjectService.Json).Length > MaximumBytes)
            throw new OperationException("watch_template_limit", "Canonical template exceeds 256 KiB.");
    }
    public static byte[] Bytes(WatchTemplate template) { Validate(template); return JsonSerializer.SerializeToUtf8Bytes(template, ProjectService.Json); }
    private static void CheckKeys(JsonElement value, int depth)
    {
        if (depth > 16) throw new JsonException("Template nesting exceeds 16 levels.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new JsonException("Duplicate object property.");
                CheckKeys(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckKeys(item, depth + 1);
    }
}

public sealed partial class ProjectService
{
    public Artifact ExportWatchTemplate(string id, WatchTemplateExportRequest request)
    {
        WatchDefinition definition;
        lock (watchLock)
        {
            definition = Clone(Watch(id).Definition)!;
            if (definition.Revision != request.ExpectedRevision) throw new OperationException("stale_revision", "Watch definition changed; refresh before exporting.");
        }
        var settings = definition.Settings;
        var bytes = WatchTemplates.Bytes(new(WatchTemplates.SchemaVersion, settings.Name, settings.Tags, settings.IntervalMs, settings.HistoryLimit, settings.Backend));
        var artifactId = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(artifactId); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "project.json"), bytes);
        var artifact = new Artifact(artifactId, id, "json", Hash(bytes), bytes.Length,
            "Portable watch selections/settings only; no source paths/hashes, controller target/routes, mode, values or recordings. Review names and tag selections before sharing.");
        Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
    }

    public async Task<WatchTemplatePreviewReport> PreviewWatchTemplateAsync(WatchTemplatePreviewRequest request, CancellationToken ct = default)
    {
        var template = WatchTemplates.Parse(request.Template);
        var maps = request.ProgramMappings ?? [];
        if (maps.Length > 64 || maps.Any(m => m is null) || maps.Select(m => m.SourceProgram).Distinct(StringComparer.Ordinal).Count() != maps.Length)
            throw new OperationException("watch_template_mapping", "Use up to 64 distinct exact source-program mappings; null denotes controller scope.");
        foreach (var map in maps)
        {
            if (map.SourceProgram is not null) WatchValidation.Identifier(map.SourceProgram);
            if (map.DestinationProgram is not null) WatchValidation.Identifier(map.DestinationProgram);
            if (!template.Tags.Any(t => t.ProgramName == map.SourceProgram)) throw new OperationException("watch_template_mapping", "A mapping names a scope absent from the template.");
        }
        var tags = template.Tags.Select(tag => maps.SingleOrDefault(m => m.SourceProgram == tag.ProgramName) is { } map ? tag with { ProgramName = map.DestinationProgram } : tag).ToArray();
        var proposed = new BrowserWatchRequest(request.ProjectId, request.ExpectedSha256, request.Name ?? template.Name, tags, request.Mode,
            request.ControllerTargetId, template.IntervalMs, request.Backend ?? template.Backend, template.HistoryLimit);
        var preflight = await PreflightWatchAsync(new WatchPreflightRequest(Browser: proposed), ct);
        var blockers = preflight.Checks.Where(c => c.Status != "passed" && c.Name is not ("reader_configuration" or "controller_access"))
            .Select(c => c.Issue ?? new WatchPreflightIssue("watch_template_binding", c.Name + " was not checked.")).ToList();
        foreach (var selection in preflight.Selections.Where(s => s.Status != "supported"))
            blockers.Add(new("watch_template_selection", "Selection " + selection.Tag.Key + " is " + selection.Status + ": " + string.Join("; ", selection.Issues.Select(i => i.Message))));
        var templateHash = Hash(WatchTemplates.Bytes(template));
        var reviewHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { templateSha256 = templateHash, proposed }, Json));
        return new(templateHash, reviewHash, proposed, blockers.Count == 0, blockers.ToArray(), preflight,
            "Preview creates no artifact/list and never connects. Import rechecks this exact template, destination hash, mapped selections and policy, then creates a new stopped list. Reader availability and controller enablement are start checks; they do not prevent saving a valid stopped list. No existing list/recording is overwritten; no controller operation is authorized.");
    }

    public async Task<WatchStatus> ImportWatchTemplateAsync(WatchTemplateImportRequest request, CancellationToken ct = default)
    {
        var preview = await PreviewWatchTemplateAsync(request.Preview, ct);
        if (!string.Equals(preview.ReviewSha256, request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "Template/binding differs from the reviewed preview. Preview it again before importing.");
        if (!preview.CanImport) throw new OperationException("watch_template_binding", "Resolve template binding diagnostics before creating the stopped list.");
        return await CreateBrowserWatchAsync(preview.Proposed, ct);
    }
}
