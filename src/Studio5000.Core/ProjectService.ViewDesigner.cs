using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private string VdRoot => Path.Combine(state, "viewdesigner");
    private string VdDir(string id) => Path.Combine(VdRoot, Id(id));
    private string VdMeta(string id) => Path.Combine(VdDir(id), "project.json");
    private string VdRevision(ProjectInfo p) => Path.Combine(VdDir(p.Id), "revision-" + p.Revision);

    public object ViewDesignerStatus()
    {
        var installed = File.Exists(options.ViewDesignerPath);
        return new
        {
            installed, executable = options.ViewDesignerPath,
            fileVersion = installed ? FileVersionInfo.GetVersionInfo(options.ViewDesignerPath).FileVersion : null,
            format = "View Designer exported folder with ViewApplication.hmi (9324-RM001, v9.01)",
            supportedOperations = new[] { "snapshot_export", "inspect_files", "inspect_objects_and_properties", "preview_existing_property_edits", "apply_working_copy", "prepare_import_zip", "compare_reexport" },
            preservedContent = "All exported assets and untouched .hmi bytes, encoding and line endings",
            validation = "Structural reader; property types, catalog membership, references and build validity require View Designer.",
            nativeImportStatus = "not_observed_by_service", directVpdEditing = false,
            automatedNativeImport = false, runtimeDownload = false
        };
    }

    public ProjectInfo[] ViewDesignerProjects() => Directory.Exists(VdRoot)
        ? Directory.GetDirectories(VdRoot).Where(d => File.Exists(Path.Combine(d, "project.json")))
            .Select(d => Read<ProjectInfo>(Path.Combine(d, "project.json"))).OrderByDescending(p => p.CreatedAt).ToArray() : [];

    public ProjectInfo ViewDesignerProject(string id)
    {
        var p = Read<ProjectInfo>(VdMeta(id));
        if (TreeHash(ReadVdTree(VdRevision(p))) != p.CurrentSha256) throw new OperationException("state_tampered", "View Designer working tree differs from its manifest.");
        return p;
    }

    public async Task<ProjectInfo> RegisterViewDesignerAsync(string source, CancellationToken ct = default)
    {
        if (Path.GetFileName(source).Equals("ViewApplication.hmi", StringComparison.OrdinalIgnoreCase))
            source = Path.GetDirectoryName(AllowedSource(source))!;
        source = AllowedSource(source, directory: true);
        await gate.WaitAsync(ct);
        try
        {
            var files = ReadVdTree(source); ValidateVdTree(files);
            var hash = TreeHash(files); var id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(VdDir(id));
            var project = new ProjectInfo(id, new DirectoryInfo(source).Name, source, hash, hash, 0, DateTimeOffset.UtcNow, "view-designer");
            WriteVdTree(Path.Combine(VdDir(id), "original"), files); WriteVdTree(VdRevision(project), files);
            Save(VdMeta(id), project); Audit("viewdesigner_register", project, null, hash, "Export tree snapshotted; native project untouched.");
            return project;
        }
        finally { gate.Release(); }
    }

    public ViewDesignerInspection InspectViewDesigner(string id)
    {
        var p = ViewDesignerProject(id); var files = ReadVdTree(VdRevision(p));
        return new(p, files.Select(f => new ViewDesignerFile(f.Key, Hash(f.Value), f.Value.Length, IsHmi(f.Key))).ToArray());
    }

    public ViewDesignerContent ReadViewDesignerFile(string id, string file)
    {
        var p = ViewDesignerProject(id); var tree = ReadVdTree(VdRevision(p));
        var bytes = VdFile(tree, file);
        if (!IsHmi(file)) throw new OperationException("unsupported_file", "Only .hmi files have editable properties; other assets are preserved unchanged.");
        return new(file, Hash(bytes), p.CurrentSha256, ViewDesignerHmi.Parse(bytes));
    }

    public async Task<ViewDesignerPreview> PreviewViewDesignerAsync(string id, ViewDesignerPreviewRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var p = ViewDesignerProject(id); Expect(p, request.ExpectedSha256);
            if (request.Edits is null || request.Edits.Length is < 1 or > 256) throw new OperationException("invalid_edits", "Supply 1–256 existing-property edits.");
            var files = ReadVdTree(VdRevision(p)); var diff = new StringBuilder();
            foreach (var group in request.Edits.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
            {
                var bytes = VdFile(files, group.Key);
                if (!IsHmi(group.Key)) throw new OperationException("unsupported_file", "Only existing .hmi properties can be edited.");
                var doc = ViewDesignerHmi.Parse(bytes);
                foreach (var edit in group)
                {
                    var before = doc.Properties.SingleOrDefault(x => x.Selector.Equals(edit.Selector, StringComparison.OrdinalIgnoreCase))
                        ?? throw new OperationException("not_found", "Property selector not found: " + edit.Selector);
                    diff.AppendLine(group.Key + " " + before.Selector).AppendLine("- " + before.Value).AppendLine("+ " + edit.Value);
                }
                files[group.Key] = ViewDesignerHmi.Apply(bytes, group.ToArray());
            }
            ValidateVdTree(files);
            var resultHash = TreeHash(files);
            if (resultHash == p.CurrentSha256) throw new OperationException("no_changes", "Requested values leave the tree unchanged.");
            var draft = new ViewDesignerPreview(Guid.NewGuid().ToString("N"), id, p.CurrentSha256, resultHash, request.Edits, diff.ToString(), DateTimeOffset.UtcNow);
            Save(Path.Combine(VdDir(id), draft.Id + ".draft.json"), draft);
            return draft;
        }
        finally { gate.Release(); }
    }

    public async Task<ProjectInfo> ApplyViewDesignerAsync(string id, string previewId, ApplyRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var p = ViewDesignerProject(id); Expect(p, request.ExpectedSha256);
            var draft = Read<ViewDesignerPreview>(Path.Combine(VdDir(id), Id(previewId) + ".draft.json"));
            if (draft.ProjectId != id || draft.BaseSha256 != p.CurrentSha256) throw new OperationException("stale_revision", "Preview is for a different revision.");
            var files = ReadVdTree(VdRevision(p));
            foreach (var group in draft.Edits.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
                files[group.Key] = ViewDesignerHmi.Apply(VdFile(files, group.Key), group.ToArray());
            ValidateVdTree(files);
            if (TreeHash(files) != draft.ResultSha256) throw new OperationException("draft_tampered", "Preview edits no longer produce the reviewed tree hash.");
            var next = p with { Revision = p.Revision + 1, CurrentSha256 = draft.ResultSha256 };
            WriteVdTree(VdRevision(next), files); Save(VdMeta(id), next);
            Audit("viewdesigner_apply", next, p.CurrentSha256, next.CurrentSha256, draft.Id);
            return next;
        }
        finally { gate.Release(); }
    }

    public async Task<ViewDesignerImportPlan> PrepareViewDesignerImportAsync(string id, ViewDesignerImportRequest request, CancellationToken ct = default)
    {
        foreach (var context in new[] { request.TargetProject, request.PanelType, request.Language })
            if (string.IsNullOrWhiteSpace(context) || context.Length > 256 || context.Any(char.IsControl)) throw new OperationException("invalid_context", "Specify target project, panel type and language.");
        await gate.WaitAsync(ct);
        try
        {
            var p = ViewDesignerProject(id); Expect(p, request.ExpectedSha256);
            var files = ReadVdTree(VdRevision(p)); ValidateVdTree(files);
            var original = ReadVdTree(Path.Combine(VdDir(id), "original"));
            if (TreeHash(original) != p.SourceSha256) throw new OperationException("state_tampered", "Original export differs from its manifest.");
            var changes = new List<ViewDesignerEdit>();
            foreach (var f in files.Where(f => IsHmi(f.Key)))
            {
                var baseline = ViewDesignerHmi.Parse(original[f.Key]);
                foreach (var property in ViewDesignerHmi.Parse(f.Value).Properties)
                    if (baseline.Properties.Single(x => x.Selector == property.Selector).Value != property.Value)
                        changes.Add(new(f.Key, property.Selector, property.Value));
            }
            var planId = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(planId); Directory.CreateDirectory(dir);
            var tree = Path.Combine(dir, "package"); Directory.CreateDirectory(tree);
            var export = Path.Combine(tree, "import"); WriteVdTree(export, files);
            WriteVdTree(Path.Combine(tree, "original-export"), original);
            var reexport = Path.Combine(dir, "reexport"); Directory.CreateDirectory(reexport);
            var instructions = "Save a separate native VPD backup. Confirm exact target project, same panel type and language. " +
                "In View Designer use File > Import Project and select import/ViewApplication.hmi. Same-named elements are overwritten. " +
                "Inspect import messages and screens, build and save natively, then File > Export Project into the plan reexport directory. " +
                "Language-file import is separate when changing language. No runtime download is performed by this service. " +
                "This package has structural checks only; catalog, binding and runtime validation remain pending.";
            Save(Path.Combine(tree, "handoff.json"), new { project = p, request, changes, instructions, nativeImportStatus = "pending" });
            File.WriteAllText(Path.Combine(tree, "README.txt"), instructions);
            var zip = Path.Combine(dir, "project.zip"); ZipFile.CreateFromDirectory(tree, zip);
            var artifact = new Artifact(planId, id, "zip", Hash(File.ReadAllBytes(zip)), new FileInfo(zip).Length,
                "View Designer export structure checked; native import, build and runtime validation pending.");
            var plan = new ViewDesignerImportPlan(planId, id, p.CurrentSha256, request.TargetProject, request.PanelType, request.Language,
                Path.Combine(export, "ViewApplication.hmi"), reexport, changes.ToArray(), artifact, "pending", DateTimeOffset.UtcNow);
            Save(Path.Combine(dir, "artifact.json"), artifact); Save(Path.Combine(dir, "viewdesigner-plan.json"), plan);
            Audit("viewdesigner_prepare_import", p, p.SourceSha256, p.CurrentSha256, planId); return plan;
        }
        finally { gate.Release(); }
    }

    public async Task<ViewDesignerVerification> VerifyViewDesignerImportAsync(string planId, VerifyGraphicsImportRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var dir = ArtifactDir(planId); var plan = Read<ViewDesignerImportPlan>(Path.Combine(dir, "viewdesigner-plan.json"));
            var p = ViewDesignerProject(plan.ProjectId); Expect(p, plan.ExpectedSha256);
            Download(plan.Package.Id); // verify the preserved package hash
            var prepared = ReadVdTree(Path.GetDirectoryName(plan.ImportEntryPath)!);
            if (TreeHash(prepared) != plan.ExpectedSha256) throw new OperationException("artifact_tampered", "Prepared import files changed.");
            var path = string.IsNullOrWhiteSpace(request.ReexportPath) ? plan.ReexportDirectory : Path.GetFullPath(request.ReexportPath);
            if (!path.Equals(plan.ReexportDirectory, StringComparison.OrdinalIgnoreCase)) path = AllowedSource(path, directory: true);
            RejectLinks(path);
            var files = ReadVdTree(path); ValidateVdTree(files);
            var results = plan.Changes.Select(change =>
            {
                string? actual = null;
                if (files.TryGetValue(change.File, out var bytes)) actual = ViewDesignerHmi.Parse(bytes).Properties
                    .SingleOrDefault(x => x.Selector.Equals(change.Selector, StringComparison.OrdinalIgnoreCase))?.Value;
                return new ViewDesignerPropertyResult(change.File, change.Selector, change.Value, actual, actual == change.Value);
            }).ToArray();
            var receipt = new ViewDesignerVerification(plan.Id,
                results.Length == 0 ? "no_changed_properties" : results.All(r => r.Matches) ? "property_values_match" : "property_values_mismatch",
                TreeHash(files), results, "not_observed_by_service",
                "Supplied re-export values do not authenticate native origin, target project, panel, language, build success or runtime behavior.", DateTimeOffset.UtcNow);
            var evidence = Path.Combine(dir, "evidence-" + Guid.NewGuid().ToString("N")); WriteVdTree(evidence, files);
            Save(evidence + ".json", receipt); Audit("viewdesigner_verify_export", p, plan.ExpectedSha256, receipt.ReexportSha256, receipt.Status);
            return receipt;
        }
        finally { gate.Release(); }
    }

    private static bool IsHmi(string file) => Path.GetExtension(file).Equals(".hmi", StringComparison.OrdinalIgnoreCase);
    private static byte[] VdFile(Dictionary<string, byte[]> files, string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\\') || file.Split('/').Any(s => s is "" or "." or ".."))
            throw new OperationException("path_denied", "Use an exact relative file path from inspection, with forward slashes.");
        return files.TryGetValue(file, out var bytes) ? bytes : throw new OperationException("not_found", "Exported file not found: " + file);
    }
    private static Dictionary<string, byte[]> ReadVdTree(string path)
    {
        RejectLinks(path);
        if (!Directory.Exists(path)) throw new OperationException("not_found", "Export folder does not exist.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in EnumerateSafeTree(path))
        {
            if (files.Count >= 10000 || new FileInfo(file).Length > 64 * 1024 * 1024) throw new OperationException("file_too_large", "Export folder exceeds file limits.");
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            foreach (var part in relative.Split('/')) ValidateFileName(part);
            var bytes = File.ReadAllBytes(file); total += bytes.Length;
            if (total > 128 * 1024 * 1024) throw new OperationException("file_too_large", "Export folder exceeds 128 MiB.");
            if (!files.TryAdd(relative, bytes)) throw new OperationException("invalid_hmi", "Case-ambiguous export paths.");
        }
        return files;
    }
    private static string TreeHash(Dictionary<string, byte[]> files) => Hash(JsonSerializer.SerializeToUtf8Bytes(
        files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new { path = f.Key, sha256 = Hash(f.Value), bytes = f.Value.Length }), Json));
    private static void WriteVdTree(string path, Dictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(path); RejectLinks(path);
        foreach (var f in files)
        {
            var dest = Path.Combine(path, f.Key.Replace('/', Path.DirectorySeparatorChar));
            RejectLinks(dest); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.WriteAllBytes(dest, f.Value);
        }
    }
    private static void ValidateVdTree(Dictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("ViewApplication.hmi", out var app)) throw new OperationException("invalid_hmi", "Select an exported folder containing ViewApplication.hmi. Export a VPD with File > Export Project first.");
        if (ViewDesignerHmi.Parse(app).Objects.Count(o => o.Type.Equals("ViewProject", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new OperationException("invalid_hmi", "ViewApplication.hmi must contain one ViewProject.");
        foreach (var f in files.Where(f => IsHmi(f.Key))) ViewDesignerHmi.Parse(f.Value);
    }
}
