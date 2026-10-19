using System.Text.Json;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private string HmiLanguageDir(string id) => Path.Combine(state, "hmi-languages", Id(id));
    private string HmiLanguageMeta(string id) => Path.Combine(HmiLanguageDir(id), "workspace.json");
    private string HmiLanguageRevision(HmiLanguageInfo info) => Path.Combine(HmiLanguageDir(info.Id), $"revision-{info.Revision}.{info.Format}");
    private static ProjectInfo HmiLanguageAuditProject(HmiLanguageInfo p) => new(p.Id, p.Name, p.SourcePath, p.SourceSha256, p.CurrentSha256, p.Revision, p.CreatedAt, "hmi-languages-" + p.Platform);
    public HmiLanguageInfo[] HmiLanguages() => Directory.Exists(Path.Combine(state, "hmi-languages"))
        ? Directory.GetFiles(Path.Combine(state, "hmi-languages"), "workspace.json", SearchOption.AllDirectories).Select(Read<HmiLanguageInfo>).OrderByDescending(p => p.CreatedAt).ToArray() : [];
    public HmiLanguageInfo HmiLanguage(string id)
    {
        var info = Read<HmiLanguageInfo>(HmiLanguageMeta(id));
        if (Hash(File.ReadAllBytes(HmiLanguageRevision(info))) != info.CurrentSha256) throw new OperationException("state_tampered", "Language revision hash differs from its manifest.");
        return info;
    }
    private byte[] ReadHmiLanguageSource(string path)
    {
        path = AllowedSource(path);
        if (new FileInfo(path).Length > HmiLanguageFile.MaximumBytes) throw new OperationException("hmi_language_limit", "Language files are limited to 16 MiB.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > HmiLanguageFile.MaximumBytes) throw new OperationException("hmi_language_limit", "Language files are limited to 16 MiB.");
        return bytes;
    }
    public async Task<HmiLanguageInfo> RegisterHmiLanguageAsync(HmiLanguageRegisterRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var source = AllowedSource(request.Path); var extension = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
            if (extension is not ("xls" or "xlsx" or "txt")) throw new OperationException("hmi_language_format", "Select a native .xls/.xlsx workbook or .txt language export.");
            var bytes = ReadHmiLanguageSource(source); var parsed = HmiLanguageFile.Parse(bytes, request.Platform, request.Language);
            if ((parsed.Format == "openxml") != (extension is "xls" or "xlsx")) throw new OperationException("hmi_language_format", "The file extension must match its detected workbook/text container.");
            if (parsed.Format == "openxml" && request.Language is not null || request.Platform == "view-designer" && request.Language is not null) throw new OperationException("hmi_language_format", "Matrix languages come from native column headers; Language is only for single-language FactoryTalk text.");
            var id = Guid.NewGuid().ToString("N"); var hash = Hash(bytes);
            var info = new HmiLanguageInfo(id, Path.GetFileNameWithoutExtension(source), request.Platform, extension, source, hash, hash, 0, DateTimeOffset.UtcNow, request.Language);
            Directory.CreateDirectory(HmiLanguageDir(id)); AtomicBytes(Path.Combine(HmiLanguageDir(id), "original." + extension), bytes); AtomicBytes(HmiLanguageRevision(info), bytes);
            Save(HmiLanguageMeta(id), info); Audit("hmi_language_register", HmiLanguageAuditProject(info), null, hash, HmiLanguageFile.Boundary); return info;
        }
        finally { gate.Release(); }
    }
    private (HmiLanguageInfo Info, byte[] Bytes, HmiLanguageFile File) HmiLanguageSource(string id, string expected)
    {
        var p = HmiLanguage(id);
        if (string.IsNullOrWhiteSpace(expected) || !p.CurrentSha256.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Language workspace changed; load its current revision and preview again.");
        var bytes = File.ReadAllBytes(HmiLanguageRevision(p));
        if (Hash(bytes) != p.CurrentSha256) throw new OperationException("state_tampered", "Language revision changed while reading.");
        return (p, bytes, HmiLanguageFile.Parse(bytes, p.Platform, p.Language));
    }
    public HmiLanguagePage BrowseHmiLanguage(string id, HmiLanguageQuery request)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 500 || request.Query?.Length > 256) throw new OperationException("hmi_language_query", "Choose nonnegative offset, limit 1..500 and search up to 256 characters.");
        if (request.Language is not null) HmiLanguageFile.Language(request.Language);
        var (p, _, file) = HmiLanguageSource(id, request.ExpectedSha256);
        if (request.Language is not null && !file.Languages.Contains(request.Language, StringComparer.OrdinalIgnoreCase)) throw new OperationException("hmi_language_query", "Requested language is absent from this export.");
        var selected = file.Records.Where(r => request.Query is null || string.Join(' ', r.Context.Concat(r.Texts.Where(t => request.Language is null || t.Language.Equals(request.Language, StringComparison.OrdinalIgnoreCase)).Select(t => t.Text))).Contains(request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var page = new HmiLanguagePage(p, file.Languages, selected.Skip(request.Offset).Take(request.Limit).ToArray(), selected.Length, request.Offset, request.Limit, HmiLanguageFile.Boundary);
        if (JsonSerializer.SerializeToUtf8Bytes(page, Json).Length > 4 * 1024 * 1024) throw new OperationException("hmi_language_limit", "Page exceeds 4 MiB; reduce the limit or narrow the search.");
        return page;
    }
    private (HmiLanguagePreview Preview, byte[] Bytes, HmiLanguageInfo Info) BuildHmiLanguagePreview(string id, HmiLanguagePreviewRequest request)
    {
        var (p, _, file) = HmiLanguageSource(id, request.ExpectedSha256); var update = file.Update(request);
        var resultHash = Hash(update.Bytes); var readback = HmiLanguageFile.Parse(update.Bytes, p.Platform, p.Language);
        var before = HmiLanguageFile.Parse(File.ReadAllBytes(HmiLanguageRevision(p)), p.Platform, p.Language).Records;
        var expected = before.ToDictionary(r => r.Key, r => r.Texts.ToDictionary(t => t.Language, t => t.Text, StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        foreach (var values in expected.Values) foreach (var l in update.Added) values.Add(l, "");
        foreach (var change in update.Changes) expected[change.Key][change.Language] = change.After;
        if (readback.Records.Length != before.Length || readback.Records.Any(r => !expected.TryGetValue(r.Key, out var values) || values.Count != r.Texts.Length || r.Texts.Any(t => !values.TryGetValue(t.Language, out var value) || value != t.Text)))
            throw new OperationException("hmi_language_readback", "Edited export failed independent identity/translation readback.");
        var review = Hash(JsonSerializer.SerializeToUtf8Bytes(new { id, p.Revision, p.CurrentSha256, request, resultHash }, Json));
        var preview = new HmiLanguagePreview(id, p.Revision, p.CurrentSha256, review, resultHash, update.Changes, update.Added, HmiLanguageFile.Boundary);
        if (JsonSerializer.SerializeToUtf8Bytes(preview, Json).Length > 4 * 1024 * 1024) throw new OperationException("hmi_language_limit", "Review exceeds 4 MiB; split the edits into smaller batches.");
        return (preview, update.Bytes, p);
    }
    public async Task<HmiLanguagePreview> PreviewHmiLanguageAsync(string id, HmiLanguagePreviewRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return BuildHmiLanguagePreview(id, request).Preview; } finally { gate.Release(); } }
    public async Task<HmiLanguageInfo> ApplyHmiLanguageAsync(string id, HmiLanguageApplyRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var (preview, bytes, p) = BuildHmiLanguagePreview(id, request.Preview);
            if (!preview.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Language changes differ from the exact reviewed source/text/languages.");
            var next = p with { Revision = p.Revision + 1, CurrentSha256 = preview.ResultSha256 };
            AtomicBytes(HmiLanguageRevision(next), bytes); Audit("hmi_language_apply_intent", HmiLanguageAuditProject(p), p.CurrentSha256, next.CurrentSha256, preview.ReviewSha256);
            Save(HmiLanguageMeta(id), next); Audit("hmi_language_apply", HmiLanguageAuditProject(next), p.CurrentSha256, next.CurrentSha256, $"{preview.Changes.Length} translations; {preview.AddedLanguages.Length} new columns."); return next;
        }
        finally { gate.Release(); }
    }
    public async Task<Artifact> ExportHmiLanguageAsync(string id, HmiLanguageExportRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var (p, bytes, _) = HmiLanguageSource(id, request.ExpectedSha256); var aid = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(aid); Directory.CreateDirectory(dir);
            AtomicBytes(Path.Combine(dir, "project." + p.Format), bytes);
            var artifact = new Artifact(aid, id, p.Format, Hash(bytes), bytes.Length, HmiLanguageFile.Boundary); Save(Path.Combine(dir, "artifact.json"), artifact);
            Audit("hmi_language_export", HmiLanguageAuditProject(p), p.CurrentSha256, artifact.Sha256, artifact.Id); return artifact;
        }
        finally { gate.Release(); }
    }
    public HmiLanguageVerification VerifyHmiLanguage(string id, HmiLanguageVerifyRequest request)
    {
        var (p, _, file) = HmiLanguageSource(id, request.ExpectedSha256); var bytes = ReadHmiLanguageSource(request.Path); var observed = HmiLanguageFile.Parse(bytes, p.Platform, p.Language);
        var differences = new List<HmiLanguageDifference>(); var count = 0;
        void Difference(string key, string? language, string kind, string? expected, string? actual)
        { count++; if (differences.Count < 200) differences.Add(new(key, language, kind, expected, actual)); }
        var left = file.Records.ToDictionary(r => r.Key); var right = observed.Records.ToDictionary(r => r.Key);
        foreach (var key in left.Keys.Union(right.Keys, StringComparer.Ordinal))
        {
            if (!left.TryGetValue(key, out var l)) { Difference(key, null, "unexpected-identity", null, null); continue; }
            if (!right.TryGetValue(key, out var r)) { Difference(key, null, "missing-identity", null, null); continue; }
            var lt = l.Texts.ToDictionary(t => t.Language, StringComparer.OrdinalIgnoreCase); var rt = r.Texts.ToDictionary(t => t.Language, StringComparer.OrdinalIgnoreCase);
            foreach (var lang in lt.Keys.Union(rt.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var a = lt.GetValueOrDefault(lang)?.Text; var b = rt.GetValueOrDefault(lang)?.Text;
                if (a != b) Difference(key, lang, a is null ? "unexpected-language" : b is null ? "missing-language" : "translation", a, b);
            }
        }
        var result = new HmiLanguageVerification(id, p.CurrentSha256, Hash(bytes), count == 0, count, differences.ToArray(), "Compares native identities and every language string in a supplied file. Matching content alone does not establish Studio import, file provenance, layout or runtime acceptance.");
        if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > 4 * 1024 * 1024) throw new OperationException("hmi_language_limit", "Difference details exceed 4 MiB; compare a smaller native export.");
        return result;
    }
}
