using System.Text;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ViewDesignerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ViewDesignerTests", Guid.NewGuid().ToString("N"));
    private readonly ServiceOptions options;
    private ProjectService service;
    private string Source => Path.Combine(root, "export");
    private const string Screen = "namespace ViewDesigner; using HMICatalog;\r\n/* retain */ Screen Home { Button Start { Text := \"Ready\"; Border { Width := 1; } } }";
    private const string Selector = "/Screen:Home/Button:Start/@Text";
    public ViewDesignerTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "ViewApplication.hmi"), "namespace ViewDesigner; using HMICatalog; ViewProject Demo { HomeScreen := \"Home\"; }");
        File.WriteAllBytes(Path.Combine(Source, "Home.hmi"), [..Encoding.UTF8.GetPreamble(), ..Encoding.UTF8.GetBytes(Screen)]);
        File.WriteAllBytes(Path.Combine(Source, "image.png"), [1, 2, 3, 4]);
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [Source], OutputRoots = [Path.Combine(root, "out")] };
        service = new(options);
    }

    [Fact]
    public async Task SnapshotPreviewApplyPackageAndRestartPreserveBytes()
    {
        var p = await service.RegisterViewDesignerAsync(Path.Combine(Source, "ViewApplication.hmi"));
        var original = File.ReadAllBytes(Path.Combine(Source, "Home.hmi"));
        var draft = await service.PreviewViewDesignerAsync(p.Id, new(p.CurrentSha256, [new("Home.hmi", Selector, "\"Updated 零\"")]));
        Assert.Equal(0, service.ViewDesignerProject(p.Id).Revision);
        p = await service.ApplyViewDesignerAsync(p.Id, draft.Id, new(p.CurrentSha256));
        var path = Path.Combine(options.StateDirectory, "viewdesigner", p.Id, "revision-1", "Home.hmi");
        Assert.Equal(original.AsSpan(0, 3).ToArray(), File.ReadAllBytes(path)[..3]);
        Assert.Equal(Screen.Replace("\"Ready\"", "\"Updated 零\""), Encoding.UTF8.GetString(File.ReadAllBytes(path)[3..]));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Source, "Home.hmi")));
        var plan = await service.PrepareViewDesignerImportAsync(p.Id, new(p.CurrentSha256, "Target", "5510 same panel", "en-US"));
        Assert.Single(plan.Changes); Assert.Equal("pending", plan.NativeImportStatus);
        var saved = await service.SaveArtifactAsync(plan.Package.Id, Path.Combine(root, "out", "test.zip"));
        Assert.Equal(plan.Package.Sha256, ProjectService.Hash(File.ReadAllBytes(saved.Path)));
        var tree = Path.GetDirectoryName(plan.ImportEntryPath)!;
        foreach (var f in Directory.GetFiles(tree)) File.Copy(f, Path.Combine(plan.ReexportDirectory, Path.GetFileName(f)));
        var receipt = await service.VerifyViewDesignerImportAsync(plan.Id, new());
        Assert.Equal("property_values_match", receipt.Status); Assert.Equal("not_observed_by_service", receipt.NativeImportStatus);
        File.WriteAllText(Path.Combine(plan.ReexportDirectory, "Home.hmi"), Screen);
        Assert.Equal("property_values_mismatch", (await service.VerifyViewDesignerImportAsync(plan.Id, new())).Status);
        service.Dispose(); service = new(options);
        Assert.Equal(p.CurrentSha256, service.ViewDesignerProject(p.Id).CurrentSha256);
        Assert.Contains(service.History(p.Id), x => x.Action == "viewdesigner_apply");
    }

    [Theory]
    [InlineData("\"Hi\"; Enabled := true")]
    [InlineData("1 2")]
    [InlineData("\"unterminated")]
    [InlineData("true -> \"a\" -> \"b\"")]
    [InlineData("{}")]
    [InlineData("arbitraryName")]
    public void RejectsInjectedOrInvalidPropertyValues(string value) => Assert.Throws<OperationException>(() =>
        ViewDesignerHmi.Apply(Encoding.UTF8.GetBytes(Screen), [new("Home.hmi", Selector, value)]));

    [Fact]
    public void ReadsParametersNestedBlocksBindingsAndIecEscapes()
    {
        const string text = "Screen Home { X := 0 -> \"::LGX.Count\"; Button Start { OnTouchRelease Touch { DisplayPopupCommand Go(Title := \"Say $\"Hi$\"\", Width := 400) } } }";
        var doc = ViewDesignerHmi.Parse(Encoding.UTF8.GetBytes(text));
        Assert.Equal(3, doc.Properties.Length);
        Assert.Contains(doc.Properties, x => x.Selector.EndsWith("/parameter:Title") && x.Value.Contains("$\"Hi"));
        var bytes = ViewDesignerHmi.Apply(Encoding.UTF8.GetBytes(text), [new("x", "/Screen:Home/@X", "-1.25 -> \"::LGX.Other\"")]);
        Assert.Contains("-1.25 ->", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("Screen Home { Text := \"a\"; text := \"b\"; }")]
    [InlineData("Screen Home { Button X {} button x {} }")]
    [InlineData("Screen Home { X := 1 }")]
    [InlineData("Screen Home { /* unclosed")]
    public void RejectsAmbiguousOrIncompleteDocuments(string text) => Assert.Throws<OperationException>(() => ViewDesignerHmi.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Utf16EncodingAndUnchangedNewlinesArePreserved()
    {
        byte[] bytes = [..Encoding.Unicode.GetPreamble(), ..Encoding.Unicode.GetBytes(Screen)];
        var updated = ViewDesignerHmi.Apply(bytes, [new("Home.hmi", Selector, "\"Done\"")]);
        Assert.Equal(bytes[..2], updated[..2]); Assert.Contains("\r\n/* retain */", Encoding.Unicode.GetString(updated[2..]));
    }

    [Fact]
    public async Task RejectsStaleWritesTraversalAndTamperedAssets()
    {
        var p = await service.RegisterViewDesignerAsync(Source);
        var draft = await service.PreviewViewDesignerAsync(p.Id, new(p.CurrentSha256, [new("Home.hmi", Selector, "\"Updated\"")]));
        await service.ApplyViewDesignerAsync(p.Id, draft.Id, new(p.CurrentSha256));
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyViewDesignerAsync(p.Id, draft.Id, new(p.CurrentSha256)))).Code);
        Assert.Equal("path_denied", Assert.Throws<OperationException>(() => service.ReadViewDesignerFile(p.Id, "../Home.hmi")).Code);
        File.WriteAllBytes(Path.Combine(options.StateDirectory, "viewdesigner", p.Id, "revision-1", "image.png"), [9]);
        Assert.Equal("state_tampered", Assert.Throws<OperationException>(() => service.ViewDesignerProject(p.Id)).Code);
    }

    [Fact]
    public async Task RejectsMissingEntryAndOutsideSourceRoot()
    {
        File.Delete(Path.Combine(Source, "ViewApplication.hmi"));
        Assert.Equal("invalid_hmi", (await Assert.ThrowsAsync<OperationException>(() => service.RegisterViewDesignerAsync(Source))).Code);
        Assert.Equal("path_denied", (await Assert.ThrowsAsync<OperationException>(() => service.RegisterViewDesignerAsync(root))).Code);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
