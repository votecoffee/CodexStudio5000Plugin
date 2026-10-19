using System.Text.Json;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private const string TransactionBoundary = "One managed offline workspace per transaction. Stages are private until one atomic head-manifest commit. No native Studio, controller, file-save or cross-workspace distributed transaction is included.";
    private readonly object transactionSync = new();
    private readonly Dictionary<string, string> pendingTransactions = new(StringComparer.Ordinal);
    private string TransactionRoot => Path.Combine(state, "transactions");
    private string TransactionDir(string id) => Path.Combine(TransactionRoot, Id(id));
    private string TransactionMeta(string id) => Path.Combine(TransactionDir(id), "transaction.json");
    private string TransactionStagePath(string id, int version) => Path.Combine(TransactionDir(id), "stage-" + version);
    private static string WorkspaceKey(string id) => "workspace:" + Id(id);
    public WorkspaceConcurrencyStatus Concurrency() => gate.Status();
    private async Task<IDisposable> EnterWorkspaceAsync(string id, CancellationToken ct, params string[] additional)
    {
        id = Id(id); var lease = await gate.EnterAsync([WorkspaceKey(id), ..additional], ct);
        try { RecoverWorkspaceTransaction(id); return lease; } catch { lease.Dispose(); throw; }
    }
    private void InitializeTransactions()
    {
        Directory.CreateDirectory(TransactionRoot); RejectLinks(TransactionRoot);
        foreach (var file in Directory.GetFiles(TransactionRoot, "transaction.json", SearchOption.AllDirectories))
        {
            var info = ReadTransaction(Path.GetFileName(Path.GetDirectoryName(file))!);
            if (info.Status != "committing") continue;
            if (!pendingTransactions.TryAdd(info.WorkspaceId, info.Id)) throw new OperationException("transaction_recovery_required", "Multiple interrupted commits refer to one workspace; inspect preserved transaction records.");
        }
        RecoverTransactions();
    }
    private void RecoverTransactions()
    { string[] ids; lock (transactionSync) ids = pendingTransactions.Keys.ToArray(); foreach (var id in ids) RecoverWorkspaceTransaction(id); }
    private void RecoverWorkspaceTransaction(string workspaceId)
    {
        string? id; lock (transactionSync) pendingTransactions.TryGetValue(workspaceId, out id);
        if (id is null) return; var info = ReadTransaction(id);
        if (info.Status == "committing")
        {
            var (p, _) = TransactionWorkspace(info.WorkspaceType, workspaceId);
            if (p.Revision == info.CommittedRevision && p.CurrentSha256 == info.StagedSha256)
                info = info with { Status = "committed", UpdatedAt = DateTimeOffset.UtcNow };
            else if (p.Revision == info.BaseRevision && p.CurrentSha256 == info.BaseSha256)
                info = info with { Status = "open", CommittedRevision = null, ReviewSha256 = null, CommittedAt = null, UpdatedAt = DateTimeOffset.UtcNow };
            else throw new OperationException("transaction_recovery_required", "Interrupted commit cannot be reconciled with its exact base/result revision. Writes are blocked; inspect preserved transaction and workspace files.");
            Save(TransactionMeta(id), info);
        }
        lock (transactionSync) pendingTransactions.Remove(workspaceId);
    }
    private WorkspaceTransactionInfo ReadTransaction(string id)
    {
        var path = TransactionMeta(id);
        if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) throw new OperationException("state_tampered", "Transaction manifest exceeds its bound.");
        var t = Read<WorkspaceTransactionInfo>(path);
        if (t.Id != Id(id) || t.WorkspaceId != Id(t.WorkspaceId) || t.WorkspaceType is not ("project" or "hmi-languages" or "view-designer") || t.Version is < 0 or > 50 || t.Steps is null || t.Steps.Length != t.Version || t.Status is not ("open" or "committing" or "committed" or "aborted"))
            throw new OperationException("state_tampered", "Transaction manifest identity, version or state is invalid.");
        if (t.Status is "committing" or "committed" && (t.CommittedRevision != (long)t.BaseRevision + 1 || t.CommittedAt is null || t.ReviewSha256?.Length != 64))
            throw new OperationException("state_tampered", "Commit intent/receipt is incomplete.");
        return t;
    }
    private (ProjectInfo Info, HmiLanguageInfo? Language) TransactionWorkspace(string type, string id) => type switch
    {
        "project" => (Get(id), null), "view-designer" => (ViewDesignerProject(id), null),
        "hmi-languages" => LanguageWorkspace(id),
        _ => throw new OperationException("transaction_type", "Select project, hmi-languages or view-designer.")
    };
    private (ProjectInfo, HmiLanguageInfo?) LanguageWorkspace(string id)
    { var p = HmiLanguage(id); return (HmiLanguageAuditProject(p), p); }
    private Dictionary<string, byte[]> WorkspaceContent(string type, ProjectInfo p, HmiLanguageInfo? language) => type == "view-designer"
        ? ReadVdTree(VdRevision(p)) : new(StringComparer.OrdinalIgnoreCase) { ["content"] = File.ReadAllBytes(language is null ? RevisionFile(p.Id, p.Revision, p.Kind) : HmiLanguageRevision(language)) };
    private static string TransactionContentHash(string type, Dictionary<string, byte[]> content) => type == "view-designer" ? TreeHash(content) : Hash(content["content"]);
    private Dictionary<string, byte[]> StagedContent(WorkspaceTransactionInfo t)
    {
        var files = ReadVdTree(TransactionStagePath(t.Id, t.Version));
        if (t.WorkspaceType != "view-designer" && (files.Count != 1 || !files.ContainsKey("content")) || TransactionContentHash(t.WorkspaceType, files) != t.StagedSha256)
            throw new OperationException("state_tampered", "Private transaction stage differs from its manifest.");
        return files;
    }
    public WorkspaceTransactionPage Transactions(string? status = null, int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 100 || status is not (null or "open" or "committing" or "committed" or "aborted")) throw new OperationException("transaction_query", "Choose a valid status, nonnegative offset and limit 1..100.");
        var values = Directory.GetDirectories(TransactionRoot).Where(d => File.Exists(Path.Combine(d, "transaction.json"))).Select(d => ReadTransaction(Path.GetFileName(d)))
            .Where(t => status is null || t.Status == status).OrderByDescending(t => t.CreatedAt).ToArray();
        return new(values.Skip(offset).Take(limit).ToArray(), values.Length, offset, limit);
    }
    public async Task<WorkspaceTransactionInfo> TransactionAsync(string id, CancellationToken ct = default)
    { var t = ReadTransaction(id); using (await EnterWorkspaceAsync(t.WorkspaceId, ct)) return ReadTransaction(id); }
    public async Task<WorkspaceTransactionInfo> BeginTransactionAsync(WorkspaceTransactionBeginRequest request, CancellationToken ct = default)
    {
        using (await EnterWorkspaceAsync(request.WorkspaceId, ct, "transactions:catalog"))
        {
            if (request.Name?.Length > 128) throw new OperationException("transaction_name", "Name is limited to 128 characters.");
            if (Directory.GetDirectories(TransactionRoot).Length >= 10000) throw new OperationException("transaction_limit", "Retained transaction limit reached; archive the local transaction state before beginning more.");
            var (p, language) = TransactionWorkspace(request.WorkspaceType, request.WorkspaceId); Expect(p, request.ExpectedSha256);
            var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow; var files = WorkspaceContent(request.WorkspaceType, p, language);
            var info = new WorkspaceTransactionInfo(id, p.Id, request.WorkspaceType, request.Name ?? p.Name, "open", p.Revision, p.CurrentSha256, p.CurrentSha256, 0, now, now, []);
            WriteVdTree(TransactionStagePath(id, 0), files); Save(TransactionMeta(id), info); Audit("transaction_begin", p, p.CurrentSha256, p.CurrentSha256, id); return info;
        }
    }
    private static void TransactionVersion(WorkspaceTransactionInfo t, int expected, bool open = true)
    {
        if (t.Version != expected) throw new OperationException("stale_revision", "Transaction changed; reload its version and review again.");
        if (open && t.Status != "open") throw new OperationException("transaction_closed", "Transaction is no longer open.");
    }
    private Dictionary<string, byte[]> ApplyTransactionStage(WorkspaceTransactionInfo t, WorkspaceTransactionStageRequest request, HmiLanguageInfo? language)
    {
        var files = StagedContent(t);
        var count = (request.XmlEdits?.Length ?? 0) + (request.DocumentationEdits?.Length ?? 0) + (request.LanguageEdits?.Length ?? 0) + (request.AddLanguages?.Length ?? 0) + (request.ViewDesignerEdits?.Length ?? 0);
        if (count is < 1 or > 256 || JsonSerializer.SerializeToUtf8Bytes(request, Json).Length > 2 * 1024 * 1024) throw new OperationException("transaction_limit", "Stage 1..256 edits/new languages and at most 2 MiB of request JSON.");
        if (t.WorkspaceType != "project" && (request.XmlEdits is not null || request.DocumentationEdits is not null) || t.WorkspaceType != "hmi-languages" && (request.LanguageEdits is not null || request.AddLanguages is not null) || t.WorkspaceType != "view-designer" && request.ViewDesignerEdits is not null)
            throw new OperationException("transaction_type", "Use only edit families belonging to this transaction's workspace type.");
        if (t.WorkspaceType == "project")
        {
            var p = Get(t.WorkspaceId);
            if (request.XmlEdits is { Length: > 0 }) files["content"] = ApplyDocument(p, files["content"], request.XmlEdits);
            if (request.DocumentationEdits is { Length: > 0 })
            {
                if (p.Kind != "logix") throw new OperationException("not_logix", "Documentation edits require a Logix workspace.");
                files["content"] = ProjectDocumentation.Update(files["content"], request.DocumentationEdits).Bytes;
            }
        }
        else if (t.WorkspaceType == "hmi-languages")
        {
            var parsed = HmiLanguageFile.Parse(files["content"], language!.Platform, language.Language); var before = parsed.Records;
            var updated = parsed.Update(new(t.StagedSha256, request.LanguageEdits ?? [], request.AddLanguages)); files["content"] = updated.Bytes;
            ValidateHmiLanguageReadback(before, HmiLanguageFile.Parse(files["content"], language.Platform, language.Language), updated.Changes, updated.Added);
        }
        else
        {
            foreach (var group in request.ViewDesignerEdits!.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
            {
                var bytes = VdFile(files, group.Key); if (!IsHmi(group.Key)) throw new OperationException("unsupported_file", "Stage only existing .hmi properties.");
                files[group.Key] = ViewDesignerHmi.Apply(bytes, group.ToArray());
            }
            ValidateVdTree(files);
        }
        return files;
    }
    public async Task<WorkspaceTransactionInfo> StageTransactionAsync(string id, WorkspaceTransactionStageRequest request, CancellationToken ct = default)
    {
        var seed = ReadTransaction(id);
        using (await EnterWorkspaceAsync(seed.WorkspaceId, ct))
        {
            var t = ReadTransaction(id); TransactionVersion(t, request.ExpectedVersion);
            if (t.Version >= 50) throw new OperationException("transaction_limit", "At most 50 stages per transaction.");
            var (_, language) = TransactionWorkspace(t.WorkspaceType, t.WorkspaceId); var files = ApplyTransactionStage(t, request, language); var hash = TransactionContentHash(t.WorkspaceType, files);
            if (hash == t.StagedSha256) throw new OperationException("no_changes", "Stage leaves transaction content unchanged.");
            var version = t.Version + 1; var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            var count = (request.XmlEdits?.Length ?? 0) + (request.DocumentationEdits?.Length ?? 0) + (request.LanguageEdits?.Length ?? 0) + (request.AddLanguages?.Length ?? 0) + (request.ViewDesignerEdits?.Length ?? 0);
            var next = t with { Version = version, StagedSha256 = hash, UpdatedAt = DateTimeOffset.UtcNow, Steps = [..t.Steps, new(version, t.StagedSha256, hash, Hash(bytes), count)] };
            if (next.Steps.Sum(s => s.Edits) > 1000) throw new OperationException("transaction_limit", "At most 1000 total edits/new languages per transaction.");
            if (Directory.EnumerateFiles(TransactionDir(id), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) + files.Values.Sum(b => (long)b.Length) + bytes.Length > 512L * 1024 * 1024)
                throw new OperationException("transaction_limit", "Private stages are limited to 512 MiB per transaction; use fewer stages or a smaller workspace.");
            WriteVdTree(TransactionStagePath(id, version), files); AtomicBytes(Path.Combine(TransactionDir(id), "request-" + version + ".json"), bytes);
            Save(TransactionMeta(id), next); return next;
        }
    }
    private WorkspaceTransactionPreview BuildTransactionPreview(WorkspaceTransactionInfo t)
    {
        StagedContent(t); var stages = new List<WorkspaceTransactionStageRequest>();
        foreach (var step in t.Steps)
        {
            var bytes = File.ReadAllBytes(Path.Combine(TransactionDir(t.Id), "request-" + step.Version + ".json"));
            if (Hash(bytes) != step.RequestSha256) throw new OperationException("state_tampered", "Transaction stage request differs from its manifest.");
            stages.Add(JsonSerializer.Deserialize<WorkspaceTransactionStageRequest>(bytes, Json)!);
        }
        var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { t.Id, t.WorkspaceId, t.WorkspaceType, t.BaseRevision, t.BaseSha256, t.StagedSha256, t.Version, t.Steps }, Json));
        var (p, _) = TransactionWorkspace(t.WorkspaceType, t.WorkspaceId);
        var result = new WorkspaceTransactionPreview(t, hash, t.Status == "open" && t.Version > 0 && t.BaseSha256 != t.StagedSha256 && p.Revision == t.BaseRevision && p.CurrentSha256 == t.BaseSha256, p.Revision, p.CurrentSha256, stages.ToArray(), TransactionBoundary);
        if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > 4 * 1024 * 1024) throw new OperationException("transaction_limit", "Review exceeds 4 MiB; use smaller transactions.");
        return result;
    }
    public async Task<WorkspaceTransactionPreview> PreviewTransactionAsync(string id, WorkspaceTransactionVersionRequest request, CancellationToken ct = default)
    { var seed = ReadTransaction(id); using (await EnterWorkspaceAsync(seed.WorkspaceId, ct)) { var t = ReadTransaction(id); TransactionVersion(t, request.ExpectedVersion); return BuildTransactionPreview(t); } }
    public async Task<WorkspaceTransactionInfo> CommitTransactionAsync(string id, WorkspaceTransactionCommitRequest request, CancellationToken ct = default)
    {
        var seed = ReadTransaction(id);
        using (await EnterWorkspaceAsync(seed.WorkspaceId, ct))
        {
            var t = ReadTransaction(id); TransactionVersion(t, request.ExpectedVersion, open: false);
            if (t.Status == "committed" && t.ReviewSha256 == request.ExpectedReviewSha256) return t;
            if (t.Status != "open") throw new OperationException("transaction_closed", "Transaction is closed or the commit retry differs from its receipt.");
            var preview = BuildTransactionPreview(t);
            if (!preview.CanCommit) throw new OperationException("transaction_conflict", "Workspace revision changed or transaction has no net changes; no commit was applied.");
            if (preview.ReviewSha256 != request.ExpectedReviewSha256) throw new OperationException("stale_revision", "Commit must match the exact reviewed stages and result hash.");
            var (p, language) = TransactionWorkspace(t.WorkspaceType, t.WorkspaceId); var files = StagedContent(t); var revision = checked(p.Revision + 1);
            ct.ThrowIfCancellationRequested();
            var next = p with { Revision = revision, CurrentSha256 = t.StagedSha256 };
            if (t.WorkspaceType == "view-designer") WriteVdTree(VdRevision(next), files);
            else AtomicBytes(language is null ? RevisionFile(p.Id, revision, p.Kind) : HmiLanguageRevision(language with { Revision = revision }), files["content"]);
            var committing = t with { Status = "committing", ReviewSha256 = preview.ReviewSha256, CommittedRevision = revision, CommittedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            lock (transactionSync) pendingTransactions[t.WorkspaceId] = id;
            Save(TransactionMeta(id), committing); Audit("transaction_commit_intent", p, p.CurrentSha256, t.StagedSha256, id + ":" + preview.ReviewSha256);
            // Atomic manifest replacement is the single commit point. Cancellation after this point never rolls it back.
            if (language is not null) Save(HmiLanguageMeta(p.Id), language with { Revision = revision, CurrentSha256 = t.StagedSha256 });
            else Save(t.WorkspaceType == "view-designer" ? VdMeta(p.Id) : ProjectMeta(p.Id), next);
            var committed = committing with { Status = "committed" }; Save(TransactionMeta(id), committed);
            lock (transactionSync) pendingTransactions.Remove(t.WorkspaceId);
            Audit("transaction_commit", next, p.CurrentSha256, next.CurrentSha256, id); return committed;
        }
    }
    public async Task<WorkspaceTransactionInfo> AbortTransactionAsync(string id, WorkspaceTransactionVersionRequest request, CancellationToken ct = default)
    {
        var seed = ReadTransaction(id);
        using (await EnterWorkspaceAsync(seed.WorkspaceId, ct))
        {
            var t = ReadTransaction(id); TransactionVersion(t, request.ExpectedVersion, open: false);
            if (t.Status == "aborted") return t;
            if (t.Status != "open") throw new OperationException("transaction_closed", "A committed transaction cannot be aborted.");
            t = t with { Status = "aborted", UpdatedAt = DateTimeOffset.UtcNow }; Save(TransactionMeta(id), t); return t;
        }
    }
}
