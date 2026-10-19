using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using L5xploderLib;
using L5xploderLib.Services;

namespace Studio5000.Core;

// The only owner of project mutations. REST, MCP and the desktop UI share this instance through HTTP.
public sealed partial class ProjectService : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 128 };
    private readonly ServiceOptions options;
    private readonly NativeConverter native;
    private readonly IWatchSessionFactory watchFactory;
    private readonly string state;
    private readonly string[] roots;
    private readonly string[] outputRoots;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object auditGate = new();
    private readonly FileStream instanceLock;

    public ProjectService(ServiceOptions options, IWatchSessionFactory? watchFactory = null)
    {
        this.options = options;
        if (options.ControllerTargets.Any(t => string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.CommunicationsPath)) ||
            options.ControllerTargets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != options.ControllerTargets.Length)
            throw new ArgumentException("ControllerTargets must have unique nonempty IDs and communications paths.");
        foreach (var target in options.ControllerTargets)
        {
            SdkTargetPolicies.ValidateConfiguration(target);
            if (target.CipRead is not null) CipWatchPlanning.ValidateTarget(target.CipRead);
        }
        state = Path.GetFullPath(options.StateDirectory);
        roots = options.AllowedRoots.Select(Path.GetFullPath).ToArray();
        outputRoots = options.OutputRoots.Select(Path.GetFullPath).ToArray();
        if (roots.Length == 0) throw new InvalidOperationException("At least one AllowedRoots entry is required.");
        Directory.CreateDirectory(state);
        RejectLinks(state);
        instanceLock = new FileStream(Path.Combine(state, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(Path.Combine(state, "projects"));
        Directory.CreateDirectory(Path.Combine(state, "artifacts"));
        native = new(options);
        this.watchFactory = watchFactory ?? new WatchBackendRouter(native, new CipWatchSessionFactory(options));
        try { InitializeSdkJobs(); InitializeWatches(); }
        catch { instanceLock.Dispose(); gate.Dispose(); throw; }
    }

    public object Capabilities() => new
    {
        name = WorkspaceIdentity.Name, version = WorkspaceIdentity.Version, nativeBridgeConfigured = native.Available,
        operations = new[] { "register", "inspect", "read_element", "preview", "apply", "export_l5x", "explode", "register_exploded", "audit", "export_acd" },
        editOperations = new[] { "set_text", "set_attribute", "replace_element", "add_element", "remove_element" },
        exploded = new { schemaVersion = ExplodedCompatibility.SchemaVersion, acceptsUnstampedLegacy = true,
            protectedAois = false, structuredTextXmlMetadata = false, unsafeDependencyBypass = false },
        allowedRoots = roots,
        outputRoots,
        projectKinds = new[] { "logix", "ftview-me", "ftview-se" },
        factoryTalk = FactoryTalkStatus(),
        viewDesigner = ViewDesignerStatus(),
        extensions = new { protocolVersion = 1, catalog = "/api/extensions", reload = "/api/extensions/reload", fixedMcpTools = "studio_extensions_*", activation = "Versioned trusted local packages; existing calls retain their generation" },
        sdk = SdkCapabilities(),
        watches = new { minimumIntervalMs = 100, maximumTags = 64, maximumActiveLists = 4, autoStartAfterRestart = false, structures = "SDK-native decorated XML; no inferred memory offsets",
            backends = new[] { "Sdk", "LibPlcTag" }, cipBridgeConfigured = File.Exists(options.CipBridgePath), cipPlan = "/api/projects/{id}/cip-watch-plan",
            browser = "/api/tags/browse", recording = "Durable segmented journal, default 7 days/64 MiB per list; explicit gaps, revision/time queries and CSV" },
        validation = "XML checks and installed SDK operations have distinct evidence levels. Live controller operations require an enabled, configured target; validation is operation/version-specific."
    };

    public async Task<ProjectInfo> RegisterAsync(string source, CancellationToken ct = default)
    {
        source = AllowedSource(source);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".l5x" or ".acd" or ".xml")) throw new OperationException("unsupported_file", "Select an L5X/ACD project or FactoryTalk ME/SE display XML.");
        await gate.WaitAsync(ct);
        try
        {
            if (new FileInfo(source).Length > 64 * 1024 * 1024) throw new OperationException("file_too_large", "Project exceeds the 64 MiB import limit.");
            var bytes = await File.ReadAllBytesAsync(source, ct);
            if (bytes.Length > 64 * 1024 * 1024) throw new OperationException("file_too_large", "Project exceeds the 64 MiB import limit.");
            var id = Guid.NewGuid().ToString("N");
            var dir = ProjectDir(id);
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "original" + extension), bytes, ct);
            byte[] l5x;
            if (extension == ".acd")
            {
                await native.ConvertAsync(Path.Combine(dir, "original.acd"), Path.Combine(dir, "converted.l5x"), ct);
                l5x = await File.ReadAllBytesAsync(Path.Combine(dir, "converted.l5x"), ct);
            }
            else l5x = bytes;
            var kind = "logix";
            if (extension == ".xml")
            {
                var graphics = FactoryTalkGraphics.Parse(l5x);
                kind = FactoryTalkGraphics.Kind(graphics);
                FactoryTalkGraphics.Validate(graphics, options.FactoryTalkSchemaDirectory, requireSchema: false);
            }
            else XmlProjects.Parse(l5x);
            await File.WriteAllBytesAsync(RevisionFile(id, 0, kind), l5x, ct);
            var info = new ProjectInfo(id, Path.GetFileNameWithoutExtension(source), source, Hash(bytes), Hash(l5x), 0, DateTimeOffset.UtcNow, kind);
            Save(ProjectMeta(id), info);
            Audit("register", info, null, info.CurrentSha256, "Original source bytes preserved; working copy created.");
            return info;
        }
        finally { gate.Release(); }
    }

    public ProjectInfo[] Projects()
    {
        return Directory.GetFiles(Path.Combine(state, "projects"), "project.json", SearchOption.AllDirectories)
            .Select(Read<ProjectInfo>).OrderByDescending(p => p.CreatedAt).ToArray();
    }

    public ProjectInfo Get(string id)
    {
        var info = Read<ProjectInfo>(ProjectMeta(id));
        if (Hash(File.ReadAllBytes(RevisionFile(id, info.Revision, info.Kind))) != info.CurrentSha256)
            throw new OperationException("state_tampered", "Working revision hash does not match its manifest.");
        return info;
    }

    public Inspection Inspect(string id, string? kind = null, string? query = null, int offset = 0, int limit = 200)
    {
        var info = Get(id);
        var doc = ParseDocument(info, File.ReadAllBytes(RevisionFile(id, info.Revision, info.Kind)));
        var graphics = FactoryTalkGraphics.IsFactoryTalk(info.Kind);
        var elements = doc.Descendants().Where(e => graphics
                ? e.Attribute("name") != null || e.Attribute("stateId") != null || e.Attribute("expression") != null || e.Name == "displaySettings"
                : e.Attribute("Name") != null || e.Name.LocalName is "Rung" or "Line")
            .Where(e => string.IsNullOrWhiteSpace(kind) || e.Name.LocalName.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrEmpty(query) || (graphics ? string.Join(" ", e.Attributes().Select(a => a.Value)) : (string?)e.Attribute("Name") ?? e.Name.LocalName).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(e => new ElementInfo(graphics ? FactoryTalkGraphics.StablePath(e) : XmlProjects.PathOf(e), e.Name.LocalName,
                graphics ? (string?)e.Attribute("name") ?? (string?)e.Attribute("stateId") ?? e.Name.LocalName : (string?)e.Attribute("Name") ?? (string?)e.Attribute("Number"),
                graphics ? (string?)e.Ancestors("group").FirstOrDefault()?.Attribute("name") : (string?)e.Ancestors("Program").FirstOrDefault()?.Attribute("Name"))).ToArray();
        return new(info, elements.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000)).ToArray(), elements.Length, Math.Max(0, offset));
    }

    public ElementContent ReadElement(string id, string xpath)
    {
        var info = Get(id);
        var doc = ParseDocument(info, File.ReadAllBytes(RevisionFile(id, info.Revision, info.Kind)));
        var xml = XmlProjects.Select(doc, xpath).ToString();
        if (xml.Length > 1024 * 1024) throw new OperationException("element_too_large", "Select a smaller component (maximum 1 MiB).");
        return new(xpath, xml, info.CurrentSha256);
    }

    public async Task<ChangePreview> PreviewAsync(string id, PreviewRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var info = Get(id);
            Expect(info, request.ExpectedSha256);
            var baseline = await File.ReadAllBytesAsync(RevisionFile(id, info.Revision, info.Kind), ct);
            var bytes = ApplyDocument(info, baseline, request.Edits);
            var diffParts = new List<string>();
            var intermediate = baseline;
            foreach (var edit in request.Edits)
            {
                diffParts.Add(DescribeEdit(info, intermediate, edit));
                intermediate = ApplyDocument(info, intermediate, [edit]);
            }
            var diff = string.Join("\n\n", diffParts);
            var preview = new ChangePreview(Guid.NewGuid().ToString("N"), id, info.CurrentSha256, Hash(bytes), request.Edits, diff, DateTimeOffset.UtcNow);
            Save(Path.Combine(ProjectDir(id), preview.Id + ".draft.json"), preview);
            Audit("preview", info, info.CurrentSha256, preview.ResultSha256, preview.Id);
            return preview;
        }
        finally { gate.Release(); }
    }

    public ChangePreview Draft(string id, string draftId) => Read<ChangePreview>(DraftFile(id, draftId));

    public async Task<ProjectInfo> ApplyAsync(string id, string draftId, ApplyRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var info = Get(id);
            var draft = Draft(id, draftId);
            if (draft.ProjectId != id || draft.BaseSha256 != request.ExpectedSha256)
                throw new OperationException("stale_revision", "Apply must use the preview's exact base hash.");
            if (info.CurrentSha256 == draft.ResultSha256) return info; // retry after a lost response
            Expect(info, request.ExpectedSha256);
            var bytes = ApplyDocument(info, await File.ReadAllBytesAsync(RevisionFile(id, info.Revision, info.Kind), ct), draft.Edits);
            if (Hash(bytes) != draft.ResultSha256) throw new OperationException("draft_tampered", "Draft result does not match preview hash.");
            var next = info with { CurrentSha256 = draft.ResultSha256, Revision = info.Revision + 1 };
            AtomicBytes(RevisionFile(id, next.Revision, next.Kind), bytes);
            Audit("apply_intent", info, info.CurrentSha256, next.CurrentSha256, draft.Id);
            Save(ProjectMeta(id), next); // manifest atomically selects one immutable revision
            Audit("apply", next, info.CurrentSha256, next.CurrentSha256, draft.Id);
            return next;
        }
        finally { gate.Release(); }
    }

    public async Task<Artifact> ExportAsync(string id, ExportRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var info = Get(id);
            Expect(info, request.ExpectedSha256);
            var format = request.Format.ToLowerInvariant();
            var graphics = FactoryTalkGraphics.IsFactoryTalk(info.Kind);
            if (graphics ? format != "xml" : format is not ("l5x" or "acd" or "explode"))
                throw new OperationException("unsupported_format", graphics ? "Use xml for a FactoryTalk display; prepare an import plan for a ZIP package." : "Use l5x, acd, or explode.");
            var artifactId = Guid.NewGuid().ToString("N");
            var dir = ArtifactDir(artifactId);
            Directory.CreateDirectory(dir);
            var source = RevisionFile(id, info.Revision, info.Kind);
            var validation = "XML structure only; native import/verification pending.";
            if (graphics)
            {
                var report = FactoryTalkGraphics.Validate(FactoryTalkGraphics.Parse(await File.ReadAllBytesAsync(source, ct)), options.FactoryTalkSchemaDirectory, requireSchema: false);
                validation = report.SchemaValidated ? $"Validated against installed {report.SchemaFile}; native Studio import pending." : "Graphics XML structure only; matching installed schema and native import pending.";
            }
            string output;
            if (format == "explode")
            {
                var doc = XmlProjects.Parse(await File.ReadAllBytesAsync(source, ct));
                ExplodedCompatibility.ValidateXml(doc);
                foreach (var name in doc.Descendants().Attributes("Name")) ValidateFileName(name.Value);
                var tree = Path.Combine(dir, "tree");
                Directory.CreateDirectory(tree);
                var persistence = PersistenceServiceFactory.Create(new L5xSerializationOptions
                { OmitExportDate = false }, tree);
                using (var stream = File.OpenRead(source)) L5xExploder.Explode(stream, L5xDefaultConfig.DefaultConfig, persistence);
                ExplodedCompatibility.Stamp(tree);
                output = Path.Combine(dir, "project.zip");
                ZipFile.CreateFromDirectory(tree, output);
            }
            else
            {
                output = Path.Combine(dir, "project." + format);
                if (format == "acd")
                {
                    await native.ConvertAsync(source, output, ct);
                    validation = "Native SDK SaveAs completed; controller/runtime acceptance pending.";
                }
                else File.Copy(source, output);
            }
            var artifact = new Artifact(artifactId, id, format == "explode" ? "zip" : format,
                Hash(await File.ReadAllBytesAsync(output, ct)), new FileInfo(output).Length, validation);
            Save(Path.Combine(dir, "artifact.json"), artifact);
            Audit("export", info, info.CurrentSha256, artifact.Sha256, $"{format}:{artifactId}");
            return artifact;
        }
        finally { gate.Release(); }
    }

    public async Task<ProjectInfo> RegisterExplodedAsync(string path, CancellationToken ct = default)
    {
        path = AllowedSource(path, directory: true);
        // Recompose only a private snapshot: external edits cannot race the upstream reader.
        var staging = Path.Combine(state, "recompose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var files = EnumerateSafeTree(path).ToArray();
        if (files.Sum(f => new FileInfo(f).Length) > 64 * 1024 * 1024 || files.Length > 20000)
            throw new OperationException("file_too_large", "Exploded project exceeds import limits.");
        foreach (var file in files)
        {
            var dest = Path.Combine(staging, Path.GetRelativePath(path, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        var opts = ExplodedCompatibility.ReadOptions(staging);
        foreach (var file in Directory.GetFiles(staging, "*.xml", SearchOption.AllDirectories))
        {
            using var reader = System.Xml.XmlReader.Create(file, new System.Xml.XmlReaderSettings
            { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
            ExplodedCompatibility.ValidateXml(System.Xml.Linq.XDocument.Load(reader, System.Xml.Linq.LoadOptions.PreserveWhitespace));
        }
        var output = Path.Combine(staging, "recomposed.l5x");
        L5xImploder.Implode(output, L5xDefaultConfig.DefaultConfig, PersistenceServiceFactory.Create(opts, staging));
        // The managed staging area is not a general source root; copy via the common registration core.
        XmlProjects.Parse(await File.ReadAllBytesAsync(output, ct));
        await gate.WaitAsync(ct);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var dir = ProjectDir(id);
            Directory.CreateDirectory(dir);
            var bytes = await File.ReadAllBytesAsync(output, ct);
            await File.WriteAllBytesAsync(Path.Combine(dir, "original.l5x"), bytes, ct);
            await File.WriteAllBytesAsync(RevisionFile(id, 0), bytes, ct);
            var info = new ProjectInfo(id, new DirectoryInfo(path).Name, path, Hash(bytes), Hash(bytes), 0, DateTimeOffset.UtcNow);
            Save(ProjectMeta(id), info);
            Audit("register_exploded", info, null, info.CurrentSha256, "Recomposed from a private snapshot; original.l5x is the recomposition.");
            return info;
        }
        finally { gate.Release(); }
    }

    public (Artifact Artifact, string Path) Download(string id)
    {
        var dir = ArtifactDir(id);
        var artifact = Read<Artifact>(Path.Combine(dir, "artifact.json"));
        var path = Path.Combine(dir, "project." + artifact.Format);
        if (Hash(File.ReadAllBytes(path)) != artifact.Sha256) throw new OperationException("artifact_tampered", "Artifact hash mismatch.");
        return (artifact, path);
    }

    public async Task<SavedArtifact> SaveArtifactAsync(string id, string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new OperationException("path_denied", "An absolute output path is required.");
        path = Path.GetFullPath(path);
        if (!outputRoots.Any(root => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new OperationException("path_denied", "Output is outside configured OutputRoots.");
        RejectLinks(path);
        await gate.WaitAsync(ct);
        try
        {
            var (artifact, source) = Download(id);
            if (!Path.GetExtension(path).Equals("." + artifact.Format, StringComparison.OrdinalIgnoreCase))
                throw new OperationException("unsupported_format", "Output extension must match the artifact format.");
            if (File.Exists(path)) throw new OperationException("output_exists", "Choose a new output filename; existing files are preserved.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(source, path, overwrite: false);
            if (artifact.Format == "csv") return new(path, artifact.Sha256, artifact.Bytes);
            var info = File.Exists(ProjectMeta(artifact.ProjectId)) ? Get(artifact.ProjectId) :
                File.Exists(VdMeta(artifact.ProjectId)) ? ViewDesignerProject(artifact.ProjectId) : SdkAuditProject(SdkJob(artifact.ProjectId));
            Audit("save_artifact", info, null, artifact.Sha256, path);
            return new(path, artifact.Sha256, artifact.Bytes);
        }
        finally { gate.Release(); }
    }

    public AuditEvent[] History(string? id = null)
    {
        lock (auditGate)
            return File.Exists(Path.Combine(state, "audit.jsonl"))
                ? File.ReadAllLines(Path.Combine(state, "audit.jsonl")).Select(l => JsonSerializer.Deserialize<AuditEvent>(l, Json)!)
                    .Where(e => id is null || e.ProjectId == id).TakeLast(500).ToArray() : [];
    }

    private static string DescribeEdit(ProjectInfo info, byte[] baseline, Edit edit)
    {
        var before = XmlProjects.Select(ParseDocument(info, baseline), edit.XPath).ToString();
        return $"{edit.Operation} {edit.XPath}{(edit.Attribute is null ? "" : " @" + edit.Attribute)}\nBEFORE\n{before}\nREQUESTED VALUE\n{edit.Value ?? "(remove)"}";
    }

    private string AllowedSource(string path, bool directory = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new OperationException("path_denied", "An absolute source path is required.");
        path = Path.GetFullPath(path);
        if (!roots.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new OperationException("path_denied", "Path is outside configured AllowedRoots. Add its directory in service.local.json and restart the service.");
        RejectLinks(path);
        if (directory ? !Directory.Exists(path) : !File.Exists(path)) throw new OperationException("not_found", "Source does not exist.");
        return path;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new OperationException("path_denied", "Symbolic links and directory junctions are not accepted.");
    }

    private static IEnumerable<string> EnumerateSafeTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) { foreach (var file in EnumerateSafeTree(entry)) yield return file; }
            else yield return entry;
        }
    }
    private static void ValidateFileName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        var reserved = stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || reserved)
            throw new OperationException("invalid_name", "Component Name cannot safely be used as an exploded filename.");
    }
    private static void Expect(ProjectInfo info, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || !info.CurrentSha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "Project changed. Inspect the latest revision and create a new preview.");
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Id(string value) => Guid.TryParseExact(value, "N", out var id) ? id.ToString("N") : throw new OperationException("invalid_id", "Expected a project, draft or artifact ID.");
    private string ProjectDir(string id) => Path.Combine(state, "projects", Id(id));
    private string ProjectMeta(string id) => Path.Combine(ProjectDir(id), "project.json");
    private string RevisionFile(string id, int revision, string kind = "logix") => Path.Combine(ProjectDir(id), $"revision-{revision}." + (FactoryTalkGraphics.IsFactoryTalk(kind) ? "xml" : "l5x"));
    private string DraftFile(string id, string draft) => Path.Combine(ProjectDir(id), Id(draft) + ".draft.json");
    private string ArtifactDir(string id) => Path.Combine(state, "artifacts", Id(id));
    private static T Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!
        : throw new OperationException("not_found", "Requested resource does not exist.");
    private static void Save<T>(string path, T value) => AtomicBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private static void AtomicBytes(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
    private void Audit(string action, ProjectInfo info, string? before, string? after, string detail)
    {
        var e = new AuditEvent(DateTimeOffset.UtcNow, action, info.Id, before, after, detail);
        lock (auditGate) File.AppendAllText(Path.Combine(state, "audit.jsonl"), JsonSerializer.Serialize(e) + "\n", Encoding.UTF8);
    }
    private static XDocument ParseDocument(ProjectInfo info, byte[] bytes) => FactoryTalkGraphics.IsFactoryTalk(info.Kind) ? FactoryTalkGraphics.Parse(bytes) : XmlProjects.Parse(bytes);
    private byte[] ApplyDocument(ProjectInfo info, byte[] bytes, Edit[] edits) => FactoryTalkGraphics.IsFactoryTalk(info.Kind)
        ? FactoryTalkGraphics.Apply(bytes, edits, options.FactoryTalkSchemaDirectory) : XmlProjects.Apply(bytes, edits);
    public void Dispose() { DisposeWatches(); DisposeSdkJobs(); instanceLock.Dispose(); gate.Dispose(); }
}
