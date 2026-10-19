using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ServiceSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellSettingsTests", Guid.NewGuid().ToString("N"));
    private readonly ServiceOptions running;
    private readonly ServiceSettingsStore store;
    private readonly string config;
    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node, ServiceSettingsStore.Json);
    public ServiceSettingsTests()
    {
        Directory.CreateDirectory(root); config = Path.Combine(root, "service.local.json");
        running = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [Path.Combine(root, "exports")],
            ControllerTargets = [new("fixture", @"AB_ETH-1\192.0.2.10\Backplane\0", new(["GoOnlineAsync"], "Test", "main"))] };
        var document = new JsonObject { ["ServiceUrl"] = "http://127.0.0.1:5487", ["Studio5000"] = JsonSerializer.SerializeToNode(running, ServiceSettingsStore.Json),
            ["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Warning" } }, ["CustomSetting"] = "preserve" };
        File.WriteAllText(config, document.ToJsonString()); store = new(root, "http://127.0.0.1:5487", running);
    }
    [Fact]
    public void DisabledOptionalBridgesNormalizeConfigurationBinderEmptyStringsToNull()
    {
        var options = new ServiceOptions { NativeBridgePath = "", CipBridgePath = " " };
        ServiceSettingsStore.ResolveAndValidate(options, root);
        Assert.Null(options.NativeBridgePath); Assert.Null(options.CipBridgePath);
    }
    [Fact]
    public void BroadProfileIsPureAndRemovesAllTargetMethodAndScopeRestrictions()
    {
        var before = File.ReadAllBytes(config); var snapshot = store.Read(); Assert.False(snapshot.RestartRequired);
        var profile = store.BroadLocalPreview(snapshot.Saved); var options = profile.GetProperty("Studio5000").Deserialize<ServiceOptions>(ServiceSettingsStore.Json)!;
        Assert.True(options.EnableControllerOperations && options.AllowAnyControllerPath && options.AllowAnySourcePath && options.AllowAnyOutputPath);
        Assert.Null(options.ControllerTargets[0].Policy); Assert.NotNull(running.ControllerTargets[0].Policy); Assert.False(running.EnableControllerOperations);
        Assert.Equal(before, File.ReadAllBytes(config)); Assert.Equal("preserve", profile.GetProperty("CustomSetting").GetString());
        Assert.Contains("HTTP loopback and bearer authentication", snapshot.EnforcedChecks);
    }
    [Fact]
    public void SaveCreatesExactBackupAndKeepsEffectiveOptionsUntilRestart()
    {
        var before = File.ReadAllBytes(config); var initial = store.Read(); var result = store.Save(new(initial.Revision, store.BroadLocalPreview(initial.Saved)));
        Assert.Equal(before, File.ReadAllBytes(result.BackupPath!)); Assert.True(result.Settings.RestartRequired);
        Assert.Contains("Studio5000.EnableControllerOperations", result.Settings.ChangedSettings);
        Assert.False(result.Settings.Effective.GetProperty("Studio5000").GetProperty("EnableControllerOperations").GetBoolean());
        Assert.True(result.Settings.Saved.GetProperty("Studio5000").GetProperty("EnableControllerOperations").GetBoolean()); Assert.False(running.EnableControllerOperations);
        Assert.Equal("Warning", result.Settings.Saved.GetProperty("Logging").GetProperty("LogLevel").GetProperty("Default").GetString());
        Assert.Equal("settings_conflict", Assert.Throws<OperationException>(() => store.Save(new(initial.Revision, initial.Saved))).Code);
        var restored = store.Save(new(result.Settings.Revision, initial.Saved)); Assert.False(restored.Settings.RestartRequired);
        Assert.False(ServiceSettingsStore.OpenOffline(root).Read().RestartRequired);
    }
    [Theory]
    [InlineData("https://127.0.0.1:5487")]
    [InlineData("http://192.0.2.10:5487")]
    [InlineData("http://localhost:5487")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.1:5487/path")]
    [InlineData("http://127.0.0.1:5487?key=value")]
    [InlineData("http://user:secret@127.0.0.1:5487")]
    public void BroadAccessCannotDisableLoopbackAuthenticationBoundary(string url)
    {
        var snapshot = store.Read(); var document = JsonNode.Parse(snapshot.Saved.GetRawText())!.AsObject(); document["ServiceUrl"] = url;
        Assert.Throws<ArgumentException>(() => store.Save(new(snapshot.Revision, Element(document)))); Assert.Equal(snapshot.Revision, store.Read().Revision);
    }
    [Fact]
    public void InvalidPolicyUnknownSettingAndDuplicateKeysDoNotChangeSavedSettings()
    {
        var snapshot = store.Read(); var document = JsonNode.Parse(snapshot.Saved.GetRawText())!.AsObject();
        document["Studio5000"]!["MaximumConcurrentMutations"] = 100;
        Assert.Throws<ArgumentException>(() => store.Save(new(snapshot.Revision, Element(document))));
        document["Studio5000"]!["MaximumConcurrentMutations"] = 8; document["Studio5000"]!["UnsupportedBypass"] = true;
        Assert.Throws<JsonException>(() => store.Save(new(snapshot.Revision, Element(document))));
        var duplicate = JsonSerializer.Deserialize<JsonElement>("{\"ServiceUrl\":\"http://127.0.0.1:5487\",\"serviceurl\":\"http://192.0.2.10:5487\",\"Studio5000\":{}}");
        Assert.Throws<ArgumentException>(() => store.Save(new(snapshot.Revision, duplicate))); Assert.Equal(snapshot.Revision, store.Read().Revision);
    }
    [Fact]
    public void ExplicitControllerPathsAreOptInAndDoNotReplaceNamedPolicies()
    {
        var raw = @"path:AB_ETH-1\192.0.2.10\Backplane\0";
        Assert.Equal("sdk_target_unknown", Assert.Throws<OperationException>(() => ControllerAccess.Resolve(running, raw)).Code);
        running.AllowAnyControllerPath = true; Assert.Equal(raw[5..], ControllerAccess.Resolve(running, raw)!.CommunicationsPath);
        Assert.NotNull(ControllerAccess.Resolve(running, "fixture")!.Policy); Assert.Null(ControllerAccess.Resolve(running, raw)!.Policy);
        Assert.Null(ControllerAccess.Resolve(running, null)); Assert.Throws<OperationException>(() => ControllerAccess.Resolve(running, "unconfigured-alias"));
        foreach (var invalid in new[] { "path:", "path:  ", "path:abc\nxyz", "path:" + new string('x', 2049) }) Assert.Throws<OperationException>(() => ControllerAccess.Resolve(running, invalid));
        Assert.False(running.EnableControllerOperations); // Path choice alone does not enable operations.
    }
    [Fact]
    public async Task UnrestrictedFilePolicyStillPreservesOriginalAndRejectsOverwrite()
    {
        var allowed = Path.Combine(root, "restricted"); Directory.CreateDirectory(allowed);
        var source = Path.Combine(root, "outside.L5X"); var content = "<RSLogix5000Content><Controller Name='Generated'><Tags/><Programs/></Controller></RSLogix5000Content>"; File.WriteAllText(source, content);
        var options = new ServiceOptions { StateDirectory = Path.Combine(root, "restricted-state"), AllowedRoots = [allowed], OutputRoots = [allowed] };
        using (var restricted = new ProjectService(options)) Assert.Equal("path_denied", (await Assert.ThrowsAsync<OperationException>(() => restricted.RegisterAsync(source))).Code);
        options.StateDirectory = Path.Combine(root, "broad-state"); options.AllowAnySourcePath = options.AllowAnyOutputPath = true;
        using var broad = new ProjectService(options); var project = await broad.RegisterAsync(source); var artifact = await broad.ExportAsync(project.Id, new(project.CurrentSha256));
        var destination = Path.Combine(root, "new-export.L5X"); var saved = await broad.SaveArtifactAsync(artifact.Id, destination);
        Assert.Equal(project.CurrentSha256, saved.Sha256); Assert.Equal(content, File.ReadAllText(source));
        Assert.Equal("output_exists", (await Assert.ThrowsAsync<OperationException>(() => broad.SaveArtifactAsync(artifact.Id, destination))).Code);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
