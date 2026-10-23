using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Studio5000.Core.Resources;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    public object FactoryTalkStatus()
    {
        var installed = File.Exists(options.FactoryTalkStudioPath);
        var schemaFiles = Directory.Exists(options.FactoryTalkSchemaDirectory)
            ? Directory.GetFiles(options.FactoryTalkSchemaDirectory, "Gfx-*.xsd").Select(Path.GetFileName).Order().ToArray() : [];
        var windows = Process.GetProcessesByName("VStudio").Select(p =>
        {
            using (p)
            {
                try { return new { processId = p.Id, title = p.MainWindowTitle }; }
                catch (InvalidOperationException) { return new { processId = 0, title = "Process exited during observation" }; }
            }
        }).ToArray();
        return new
        {
            editions = new[] { "ME", "SE" }, studioInstalled = installed,
            studioExecutable = options.FactoryTalkStudioPath,
            studioFileVersion = installed ? FileVersionInfo.GetVersionInfo(options.FactoryTalkStudioPath).FileVersion : null,
            schemaDirectory = options.FactoryTalkSchemaDirectory, schemaFiles, windows,
            supportedEdits = new[] { "set_attribute", "set_text", "replace_element (existing properties)" },
            nativeIntegration = "Studio Graphics Import Export Wizard through the bundled Codex skill and available Computer Use tools. The REST service prepares and checks files; it does not drive wizard controls.",
            nativeLaunchAvailable = installed,
            nativeLaunchReason = "Launch is handed to the existing user desktop shell with an explicit provider identity and retained receipt. Studio and its documents remain user-owned; native startup must be observed.",
            nativeImportStatus = "not_observed", languagePolicy = "Export and import in the same confirmed application language."
        };
    }

    public object LaunchFactoryTalkStudio()
    {
        NativeExecutionPolicy.RequireGui(options);
        return ResourceSupervisor.RequireCurrent().LaunchUserGui(options.FactoryTalkStudioPath);
    }

    public GraphicsValidation ValidateGraphics(string id)
    {
        var project = RequireGraphics(id);
        return FactoryTalkGraphics.Validate(FactoryTalkGraphics.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision, project.Kind))),
            options.FactoryTalkSchemaDirectory, requireSchema: false);
    }

    public async Task<GraphicsImportPlan> PrepareGraphicsImportAsync(string id, PrepareGraphicsImportRequest request, CancellationToken ct = default)
    {
        ValidateContext(request.ApplicationName, "application"); ValidateContext(request.Language, "language");
        ValidateFileName(request.DisplayName);
        await gate.WaitAsync(ct);
        try
        {
            var info = RequireGraphics(id); Expect(info, request.ExpectedSha256);
            var bytes = await File.ReadAllBytesAsync(RevisionFile(id, info.Revision, info.Kind), ct);
            var doc = FactoryTalkGraphics.Parse(bytes);
            var validation = FactoryTalkGraphics.Validate(doc, options.FactoryTalkSchemaDirectory, requireSchema: true);
            var original = await File.ReadAllBytesAsync(Path.Combine(ProjectDir(id), "original.xml"), ct);
            if (Hash(original) != info.SourceSha256) throw new OperationException("state_tampered", "Original graphics snapshot no longer matches its source manifest.");
            var baseline = FactoryTalkGraphics.Parse(original);
            var changes = FactoryTalkGraphics.Changes(baseline, doc);
            if (changes.Length == 0) throw new OperationException("no_changes", "Apply at least one graphics property change before preparing an update import.");
            var planId = Guid.NewGuid().ToString("N");
            var dir = ArtifactDir(planId); Directory.CreateDirectory(dir);
            var tree = Path.Combine(dir, "import"); Directory.CreateDirectory(tree);
            var displayFile = request.DisplayName + ".xml";
            var batchFile = "BatchImport_" + planId + ".xml";
            if (displayFile.Equals(batchFile, StringComparison.OrdinalIgnoreCase)) throw new OperationException("invalid_name", "Display filename conflicts with the generated batch import file.");
            var xmlPath = Path.Combine(tree, displayFile);
            await File.WriteAllBytesAsync(xmlPath, bytes, ct);
            var batchPath = Path.Combine(tree, batchFile);
            var batch = new XDocument(new XElement("gfxImport", new XElement("import",
                new XAttribute("importFile", displayFile), new XAttribute("importType", "update"), new XAttribute("display", request.DisplayName))));
            batch.Save(batchPath);
            var backup = Path.Combine(dir, "native-backup"); Directory.CreateDirectory(backup);
            var reexport = Path.Combine(dir, "reexport"); Directory.CreateDirectory(reexport);
            Directory.CreateDirectory(Path.Combine(tree, "original-export"));
            File.Copy(Path.Combine(ProjectDir(id), "original.xml"), Path.Combine(tree, "original-export", displayFile));
            Save(Path.Combine(tree, "handoff.json"), new
            {
                projectId = id, request.ExpectedSha256, info.SourceSha256, request.ApplicationName, request.DisplayName, request.Language,
                validation, changes, importType = "update", nativeImportStatus = "pending",
                instructions = "Confirm exact native application/display and current language. Close the target display, use Graphics Import Export Wizard, back up graphics, import the batch as update, inspect errors, then re-export to verify. A file/schema check does not prove native import."
            });
            var zip = Path.Combine(dir, "project.zip"); ZipFile.CreateFromDirectory(tree, zip);
            var artifact = new Artifact(planId, id, "zip", Hash(await File.ReadAllBytesAsync(zip, ct)), new FileInfo(zip).Length,
                $"{validation.SchemaFile} validation passed. Native Studio import pending; application/language are caller-supplied targets.");
            Save(Path.Combine(dir, "artifact.json"), artifact);
            var plan = new GraphicsImportPlan(planId, id, info.CurrentSha256, request.ApplicationName, request.DisplayName, request.Language,
                validation.Edition, xmlPath, batchPath, backup, Path.Combine(reexport, displayFile), validation, changes, artifact, "pending", DateTimeOffset.UtcNow);
            Save(Path.Combine(dir, "graphics-plan.json"), plan);
            Audit("ftview_prepare_import", info, info.SourceSha256, info.CurrentSha256, planId);
            return plan;
        }
        finally { gate.Release(); }
    }

    public GraphicsImportPlan GraphicsPlan(string planId)
    {
        var dir = ArtifactDir(planId);
        var plan = Read<GraphicsImportPlan>(Path.Combine(dir, "graphics-plan.json"));
        if (plan.Id != Id(planId) || !Path.GetDirectoryName(plan.XmlPath)!.Equals(Path.Combine(dir, "import"), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(plan.XmlPath) || Hash(File.ReadAllBytes(plan.XmlPath)) != plan.ExpectedSha256)
            throw new OperationException("artifact_tampered", "Prepared graphics XML does not match the import plan.");
        using var reader = System.Xml.XmlReader.Create(plan.BatchPath, new System.Xml.XmlReaderSettings
        { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var batch = XDocument.Load(reader);
        var entries = batch.Root?.Elements("import").ToArray() ?? [];
        if (batch.Root?.Name != "gfxImport" || batch.Root.Attributes().Any() || entries.Length != 1 ||
            (string?)entries[0].Attribute("importFile") != Path.GetFileName(plan.XmlPath) ||
            (string?)entries[0].Attribute("display") != plan.DisplayName || (string?)entries[0].Attribute("importType") != "update")
            throw new OperationException("artifact_tampered", "Prepared batch file no longer targets the reviewed display update.");
        return plan;
    }

    public async Task<GraphicsVerification> VerifyGraphicsImportAsync(string planId, VerifyGraphicsImportRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var plan = GraphicsPlan(planId); var info = RequireGraphics(plan.ProjectId); Expect(info, plan.ExpectedSha256);
            var path = string.IsNullOrWhiteSpace(request.ReexportPath) ? plan.ReexportPath : Path.GetFullPath(request.ReexportPath);
            if (!path.Equals(plan.ReexportPath, StringComparison.OrdinalIgnoreCase)) path = AllowedSource(path);
            RejectLinks(path);
            if (!File.Exists(path)) throw new OperationException("not_found", "Native re-export is missing. Export the target display from Studio before verifying.");
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new OperationException("file_too_large", "Re-export exceeds the 64 MiB limit.");
            var bytes = await File.ReadAllBytesAsync(path, ct); var doc = FactoryTalkGraphics.Parse(bytes);
            var validation = FactoryTalkGraphics.Validate(doc, options.FactoryTalkSchemaDirectory, requireSchema: true);
            if (validation.Edition != plan.Edition) throw new OperationException("edition_mismatch", "Re-export edition differs from the prepared import plan.");
            var results = plan.Changes.Select(change =>
            {
                string? actual = null;
                try
                {
                    var element = XmlProjects.Select(doc, change.XPath);
                    actual = change.Attribute is null ? element.Value : (string?)element.Attribute(change.Attribute);
                }
                catch (OperationException) { }
                return new GraphicsPropertyResult(change.XPath, change.Attribute, change.Value, actual, actual == change.Value);
            }).ToArray();
            var verified = new GraphicsVerification(plan.Id, info.Id, results.All(r => r.Matches) ? "property_values_match" : "property_values_mismatch",
                Hash(bytes), validation, results, "not_observed_by_service",
                "A re-export file confirms property values only. The caller must observe the exact Studio application, display, language and wizard completion; XML alone does not authenticate its native origin.", DateTimeOffset.UtcNow);
            // Preserve the actual supplied evidence independently of later edits to the external file.
            var receiptId = Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(Path.Combine(ArtifactDir(planId), "evidence-" + receiptId + ".xml"), bytes, ct);
            Save(Path.Combine(ArtifactDir(planId), "verification-" + receiptId + ".json"), verified);
            Audit("ftview_verify_export", info, plan.ExpectedSha256, verified.ReexportSha256, plan.Id + ":" + verified.Status);
            return verified;
        }
        finally { gate.Release(); }
    }

    private ProjectInfo RequireGraphics(string id)
    {
        var info = Get(id);
        if (!FactoryTalkGraphics.IsFactoryTalk(info.Kind)) throw new OperationException("not_graphics", "Select a registered FactoryTalk ME/SE display XML.");
        return info;
    }
    private static void ValidateContext(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new OperationException("invalid_context", $"Provide an explicit {label} without control characters.");
    }
}
