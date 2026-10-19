using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class WatchTemplateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WatchTemplateTests", Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly ServiceOptions options;
    private readonly BombFactory factory = new();
    private readonly ProjectService service;
    public WatchTemplateTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "fixture.l5x"); File.Copy(Path.Combine(AppContext.BaseDirectory, "WatchDemo.L5X"), source);
        var doc = XmlProjects.Parse(File.ReadAllBytes(source)); var program = doc.Descendants("Program").Single();
        var clone = new XElement(program); clone.SetAttributeValue("Name", "DestinationProgram"); program.Parent!.Add(clone); doc.Save(source);
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [Path.Combine(root, "exports")],
            ControllerTargets = [new("configured", "private-linx-route", new(["GoOnlineAsync", "GetTagValueAsync"], "WatchProgram"), new("192.0.2.1", "1,0"))] };
        service = new(options, factory);
    }
    private static WatchTemplate Template() => new(1, "Portable demo", [new("count", "Record", "WatchProgram", "Inner.Count"), new("array", "Records", "WatchProgram", "[1].Inner.Count")], 375, 37);
    private static JsonElement Json(WatchTemplate template) => JsonSerializer.SerializeToElement(template, ProjectService.Json);
    private WatchListRequest Settings() => new("Portable demo", Template().Tags, ProjectService.Hash(File.ReadAllBytes(source)), SourcePath: source,
        ControllerTargetId: "configured", Mode: "Offline", IntervalMs: 375, HistoryLimit: 37);
    private async Task<WatchTemplatePreviewRequest> Request(WatchTemplate? template = null)
    { var project = await service.RegisterAsync(source); return new(Json(template ?? Template()), project.Id, project.CurrentSha256); }
    private Dictionary<string, string> Files() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("service.lock", StringComparison.Ordinal))
        .ToDictionary(p => p, p => ProjectService.Hash(File.ReadAllBytes(p)), StringComparer.Ordinal);
    [Fact]
    public async Task ExportCopiesOnlyPortableFieldsEvenWhenSourceHasDisappeared()
    {
        var watch = await service.SaveWatchAsync(Settings()); File.Delete(source);
        var artifact = service.ExportWatchTemplate(watch.Definition.Id, new(0)); var bytes = File.ReadAllBytes(service.Download(artifact.Id).Path);
        var text = System.Text.Encoding.UTF8.GetString(bytes); var json = JsonSerializer.Deserialize<JsonElement>(bytes);
        Assert.Equal(["schemaVersion", "name", "tags", "intervalMs", "historyLimit", "backend"], json.EnumerateObject().Select(p => p.Name));
        foreach (var forbidden in new[] { root, "sourcePath", "sourceArtifactId", "expectedSha256", "configured", "private-linx-route", "controllerTargetId", "mode", "latest", "recording", "192.0.2.1" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        var parsed = WatchTemplates.Parse(json); Assert.Equal(37, parsed.HistoryLimit); Assert.Equal(Template().Tags, parsed.Tags);
        Assert.Equal(artifact.Sha256, ProjectService.Hash(bytes)); Assert.Equal("json", artifact.Format); Assert.Equal(0, factory.Opens);
        var output = Path.Combine(root, "exports", "process.watch.json"); var saved = await service.SaveArtifactAsync(artifact.Id, output);
        Assert.Equal(artifact.Sha256, saved.Sha256); Assert.Equal(bytes, File.ReadAllBytes(output));
        Assert.Equal("output_exists", (await Assert.ThrowsAsync<OperationException>(() => service.SaveArtifactAsync(artifact.Id, output))).Code);
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.ExportWatchTemplate(watch.Definition.Id, new(1))).Code);
    }
    [Fact]
    public async Task PreviewCreatesNoStateAndValidImportCreatesAnIndependentStoppedListWithAllSettings()
    {
        var request = await Request(); var before = Files(); var preview = await service.PreviewWatchTemplateAsync(request);
        Assert.True(preview.CanImport); Assert.Equal("blocked", preview.Preflight.Status); // Reader is absent; stopped creation remains valid.
        Assert.Equal(before, Files()); Assert.Empty(service.WatchLists()); Assert.Equal(64, preview.ReviewSha256.Length);
        var imported = await service.ImportWatchTemplateAsync(new(request, preview.ReviewSha256));
        Assert.Equal("stopped", imported.State); Assert.Null(imported.Latest); Assert.Equal(375, imported.Definition.Settings.IntervalMs); Assert.Equal(37, imported.Definition.Settings.HistoryLimit);
        Assert.NotNull(imported.Definition.Settings.SourceArtifactId); Assert.Null(imported.Definition.Settings.SourcePath); Assert.Null(imported.Definition.Settings.ControllerTargetId);
        Assert.Equal("Offline", imported.Definition.Settings.Mode); Assert.Equal(Template().Tags, imported.Definition.Settings.Tags); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task ExactProgramMappingIsReviewedAndPolicyStillAppliesToMappedScope()
    {
        var request = await Request(); request = request with { ProgramMappings = [new("WatchProgram", "DestinationProgram")], Name = "Mapped demo" };
        var preview = await service.PreviewWatchTemplateAsync(request); Assert.True(preview.CanImport);
        Assert.All(preview.Proposed.Tags, t => Assert.Equal("DestinationProgram", t.ProgramName));
        var imported = await service.ImportWatchTemplateAsync(new(request, preview.ReviewSha256)); Assert.Equal("Mapped demo", imported.Definition.Settings.Name);
        var denied = await service.PreviewWatchTemplateAsync(request with { Mode = "Online", ControllerTargetId = "configured" });
        Assert.False(denied.CanImport); Assert.All(denied.Preflight.Selections, s => Assert.Contains(s.Issues, i => i.Code == "sdk_target_policy"));
        await Assert.ThrowsAsync<OperationException>(() => service.ImportWatchTemplateAsync(new(request with { Mode = "Online", ControllerTargetId = "configured" }, denied.ReviewSha256)));
        Assert.Single(service.WatchLists()); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task NullMappingMeansExplicitControllerScopeAndDoesNotBypassPolicy()
    {
        var request = (await Request()) with { ProgramMappings = [new("WatchProgram", null)], Mode = "Online", ControllerTargetId = "configured" };
        var preview = await service.PreviewWatchTemplateAsync(request); Assert.False(preview.CanImport); Assert.All(preview.Proposed.Tags, t => Assert.Null(t.ProgramName)); Assert.Equal(0, factory.Opens);
        request = (await Request(Template() with { Tags = [new("count", "Record", null, "Inner.Count")] })) with { ProgramMappings = [new(null, "DestinationProgram")] };
        preview = await service.PreviewWatchTemplateAsync(request); Assert.True(preview.CanImport); Assert.Equal("DestinationProgram", Assert.Single(preview.Proposed.Tags).ProgramName);
    }
    [Fact]
    public async Task AnyChangedBindingOrTemplateNeedsAnotherReviewAndCreatesNoPartialArtifacts()
    {
        var request = await Request(); var preview = await service.PreviewWatchTemplateAsync(request); var before = Files();
        foreach (var changed in new[] { request with { Name = "Different name" }, request with { Backend = "LibPlcTag", Mode = "Online", ControllerTargetId = "configured" },
            request with { Template = Json(Template() with { HistoryLimit = 38 }) }, request with { ProgramMappings = [new("WatchProgram", "DestinationProgram")] } })
            Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ImportWatchTemplateAsync(new(changed, preview.ReviewSha256)))).Code);
        Assert.Equal(before, Files()); Assert.Empty(service.WatchLists()); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task ChangedProjectHashAndMissingSelectionsBlockImportWithoutCreatingAList()
    {
        var request = await Request(); var preview = await service.PreviewWatchTemplateAsync(request);
        var edit = await service.PreviewAsync(request.ProjectId, new(request.ExpectedSha256, [new("set_attribute", "//Program[@Name='WatchProgram']/Tags/Tag[@Name='Enabled']", "true", "Constant")]));
        await service.ApplyAsync(request.ProjectId, edit.Id, new(request.ExpectedSha256));
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ImportWatchTemplateAsync(new(request, preview.ReviewSha256)))).Code);
        request = await Request(Template() with { Tags = [new("missing", "MissingTag", "WatchProgram")] }); preview = await service.PreviewWatchTemplateAsync(request);
        Assert.False(preview.CanImport); Assert.NotEmpty(preview.ImportBlockers); Assert.Equal("watch_template_binding", (await Assert.ThrowsAsync<OperationException>(() => service.ImportWatchTemplateAsync(new(request, preview.ReviewSha256)))).Code);
        Assert.Empty(service.WatchLists()); Assert.Equal(0, factory.Opens);
    }
    [Fact]
    public async Task CipTemplateDoesNotRememberOrEnableOnlineModeAndRequiresConfiguredBinding()
    {
        var request = await Request(Template() with { Backend = "LibPlcTag" });
        Assert.Equal("watch_backend", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewWatchTemplateAsync(request))).Code);
        request = request with { Mode = "Online", ControllerTargetId = "configured" }; var preview = await service.PreviewWatchTemplateAsync(request);
        Assert.True(preview.CanImport); Assert.Contains(preview.Preflight.Checks, c => c.Issue?.Code == "sdk_controller_disabled");
        var watch = await service.ImportWatchTemplateAsync(new(request, preview.ReviewSha256)); Assert.Equal("stopped", watch.State); Assert.False(options.EnableControllerOperations); Assert.Equal(0, factory.Opens);
    }
    [Theory]
    [InlineData("{\"schemaVersion\":2,\"name\":\"Future\",\"tags\":[{\"key\":\"a\",\"tagName\":\"A\"}]}", "watch_template_version")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"Duplicate\",\"name\":\"Other\",\"tags\":[]}", "watch_template_invalid")]
    [InlineData("{\"SchemaVersion\":1,\"name\":\"Wrong case\",\"tags\":[]}", "watch_template_invalid")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"Private\",\"tags\":[],\"controllerTargetId\":\"unexpected\"}", "watch_template_invalid")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"Private\",\"tags\":[{\"key\":\"a\",\"tagName\":\"A\",\"value\":42}]}", "watch_template_invalid")]
    public void RejectsFutureSchemasDuplicatesUnknownFieldsAndEmbeddedRuntimeData(string json, string code)
        => Assert.Equal(code, Assert.Throws<OperationException>(() => WatchTemplates.Parse(JsonSerializer.Deserialize<JsonElement>(json))).Code);
    [Fact]
    public async Task UnusedDuplicateAndInvalidProgramMappingsAreRejected()
    {
        var request = await Request();
        foreach (var maps in new WatchTemplateProgramMap[][] { [new("Absent", "DestinationProgram")], [new("WatchProgram", "X"), new("WatchProgram", "Y")], [new("WatchProgram", "Bad.Program")] })
            await Assert.ThrowsAsync<OperationException>(() => service.PreviewWatchTemplateAsync(request with { ProgramMappings = maps }));
        Assert.Empty(service.WatchLists());
        var large = JsonNode.Parse(Json(Template()).GetRawText())!; large["padding"] = new string('x', WatchTemplates.MaximumBytes);
        Assert.Equal("watch_template_limit", Assert.Throws<OperationException>(() => WatchTemplates.Parse(JsonSerializer.SerializeToElement(large))).Code);
    }
    private sealed class BombFactory : IWatchSessionFactory
    {
        public int Opens;
        public Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct) { Opens++; throw new InvalidOperationException("Template operation opened a reader."); }
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
