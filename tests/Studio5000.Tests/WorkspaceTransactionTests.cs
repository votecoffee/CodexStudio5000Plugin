using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class WorkspaceTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "workspace-transactions-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceOptions options;
    private ProjectService service;
    public WorkspaceTransactionTests()
    {
        Directory.CreateDirectory(root); options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] }; service = new(options);
    }
    private string Example(string name) { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + Path.GetExtension(name)); File.Copy(Path.Combine(AppContext.BaseDirectory, name), path); return path; }
    private async Task<ProjectInfo> Project() => await service.RegisterAsync(Example("MultilingualDemo.L5X"));
    private Task<WorkspaceTransactionInfo> Begin(ProjectInfo p) => service.BeginTransactionAsync(new(p.Id, "project", p.CurrentSha256));
    private static WorkspaceTransactionStageRequest Xml(int version, string name) => new(version, XmlEdits: [new("set_text", "//Program[@Name='Demo']/Description", name)]);
    [Fact]
    public async Task MultipleStagesStayPrivateAndCommitAsOneRevisionWithDurableRetryReceipt()
    {
        var p = await Project(); var t = await Begin(p);
        t = await service.StageTransactionAsync(t.Id, Xml(0, "TransactionDemo"));
        t = await service.StageTransactionAsync(t.Id, new(t.Version, DocumentationEdits: [new("//Controller/Tags/Tag[@Name='Record']", "description", "es-ES", "Transacción") ]));
        Assert.Equal(p.CurrentSha256, service.Get(p.Id).CurrentSha256); Assert.Equal(0, service.Get(p.Id).Revision);
        var preview = await service.PreviewTransactionAsync(t.Id, new(t.Version)); Assert.True(preview.CanCommit); Assert.Equal(2, preview.Stages.Length);
        var request = new WorkspaceTransactionCommitRequest(t.Version, preview.ReviewSha256); var committed = await service.CommitTransactionAsync(t.Id, request);
        Assert.Equal("committed", committed.Status); Assert.Equal(1, service.Get(p.Id).Revision); Assert.Equal(t.StagedSha256, service.Get(p.Id).CurrentSha256);
        var after = service.Get(p.Id); var draft = await service.PreviewAsync(p.Id, new(after.CurrentSha256, [new("set_text", "//Program[@Name='Demo']/Description", "LaterEdit")]));
        await service.ApplyAsync(p.Id, draft.Id, new(after.CurrentSha256)); Assert.Equal(2, service.Get(p.Id).Revision);
        Assert.Equal(JsonSerializer.Serialize(committed), JsonSerializer.Serialize(await service.CommitTransactionAsync(t.Id, request))); Assert.Equal(2, service.Get(p.Id).Revision);
        service.Dispose(); service = new(options); Assert.Equal("committed", (await service.CommitTransactionAsync(t.Id, request)).Status); Assert.Equal(2, service.Get(p.Id).Revision);
        Assert.Equal(p.SourceSha256, ProjectService.Hash(File.ReadAllBytes(p.SourcePath)));
    }
    [Fact]
    public async Task InvalidBatchStaleStagesAndChangedReviewsLeaveTheHeadAndPreviousStageUntouched()
    {
        var p = await Project(); var t = await Begin(p); t = await service.StageTransactionAsync(t.Id, Xml(0, "First"));
        await Assert.ThrowsAsync<OperationException>(() => service.StageTransactionAsync(t.Id, new(t.Version, XmlEdits: [new("set_text", "//Program[@Name='Demo']/Description", "Second"), new("set_text", "//Missing", "Fail")])));
        Assert.Equal(t.StagedSha256, (await service.TransactionAsync(t.Id)).StagedSha256);
        await Assert.ThrowsAsync<OperationException>(() => service.StageTransactionAsync(t.Id, Xml(0, "Stale")));
        await Assert.ThrowsAsync<OperationException>(() => service.CommitTransactionAsync(t.Id, new(t.Version, "wrong-review")));
        Assert.Equal(p.CurrentSha256, service.Get(p.Id).CurrentSha256);
        var aborted = await service.AbortTransactionAsync(t.Id, new(t.Version)); Assert.Equal("aborted", aborted.Status);
        Assert.Equal(JsonSerializer.Serialize(aborted), JsonSerializer.Serialize(await service.AbortTransactionAsync(t.Id, new(t.Version))));
        await Assert.ThrowsAsync<OperationException>(() => service.StageTransactionAsync(t.Id, Xml(t.Version, "Closed")));
    }
    [Fact]
    public async Task CompetingTransactionsAndLegacyWritesCannotLoseAnUpdate()
    {
        var p = await Project(); var a = await Begin(p); var b = await Begin(p);
        a = await service.StageTransactionAsync(a.Id, Xml(0, "WriterA")); b = await service.StageTransactionAsync(b.Id, Xml(0, "WriterB"));
        var ap = await service.PreviewTransactionAsync(a.Id, new(a.Version)); var bp = await service.PreviewTransactionAsync(b.Id, new(b.Version));
        async Task<string> Commit(WorkspaceTransactionInfo t, string review)
        { try { await service.CommitTransactionAsync(t.Id, new(t.Version, review)); return "committed"; } catch (OperationException e) { return e.Code; } }
        var statuses = await Task.WhenAll(Task.Run(() => Commit(a, ap.ReviewSha256)), Task.Run(() => Commit(b, bp.ReviewSha256)));
        Assert.Single(statuses, s => s == "committed"); Assert.Single(statuses, s => s == "transaction_conflict"); Assert.Equal(1, service.Get(p.Id).Revision);
        var current = service.Get(p.Id); var c = await Begin(current); c = await service.StageTransactionAsync(c.Id, Xml(0, "Private"));
        var draft = await service.PreviewAsync(p.Id, new(current.CurrentSha256, [new("set_text", "//Program[@Name='Demo']/Description", "Legacy")])); await service.ApplyAsync(p.Id, draft.Id, new(current.CurrentSha256));
        Assert.False((await service.PreviewTransactionAsync(c.Id, new(c.Version))).CanCommit);
        Assert.Equal(2, service.Get(p.Id).Revision);
    }
    [Fact]
    public async Task SnapshotReadsAndIndependentWritersOverlapWithoutSharingViolationsOrPartialManifests()
    {
        var p = await Project(); var q = await Project(); using var stop = new CancellationTokenSource();
        var reader = Task.Run(() => { while (!stop.IsCancellationRequested) { var latest = service.Get(p.Id); Assert.True(latest.Revision >= 0); } });
        async Task Write(ProjectInfo project)
        {
            for (var i = 0; i < 12; i++)
            { var draft = await service.PreviewAsync(project.Id, new(project.CurrentSha256, [new("set_text", "//Program[@Name='Demo']/Description", "Writer" + i)])); project = await service.ApplyAsync(project.Id, draft.Id, new(project.CurrentSha256)); }
        }
        try { await Task.WhenAll(Task.Run(() => Write(p)), Task.Run(() => Write(q))); } finally { stop.Cancel(); await reader; }
        Assert.Equal(12, service.Get(p.Id).Revision); Assert.Equal(12, service.Get(q.Id).Revision); Assert.Equal(0, service.Concurrency().ActiveMutations);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartReconcilesInterruptedCommitAtTheAtomicManifestBoundary(bool published)
    {
        var p = await Project(); var t = await Begin(p); t = await service.StageTransactionAsync(t.Id, Xml(0, "Recovered")); var preview = await service.PreviewTransactionAsync(t.Id, new(t.Version));
        service.Dispose(); var txDir = Path.Combine(options.StateDirectory, "transactions", t.Id);
        var intent = t with { Status = "committing", CommittedRevision = 1, ReviewSha256 = preview.ReviewSha256, CommittedAt = DateTimeOffset.UtcNow };
        File.WriteAllText(Path.Combine(txDir, "transaction.json"), JsonSerializer.Serialize(intent, ProjectService.Json));
        if (published)
        {
            var projectDir = Path.Combine(options.StateDirectory, "projects", p.Id); File.Copy(Path.Combine(txDir, "stage-1", "content"), Path.Combine(projectDir, "revision-1.l5x"));
            File.WriteAllText(Path.Combine(projectDir, "project.json"), JsonSerializer.Serialize(p with { Revision = 1, CurrentSha256 = t.StagedSha256 }, ProjectService.Json));
        }
        service = new(options); var recovered = await service.TransactionAsync(t.Id); Assert.Equal(published ? "committed" : "open", recovered.Status);
        var committed = await service.CommitTransactionAsync(t.Id, new(t.Version, preview.ReviewSha256)); Assert.Equal("committed", committed.Status); Assert.Equal(1, service.Get(p.Id).Revision);
    }
    [Theory]
    [InlineData("ftview-me")]
    [InlineData("view-designer")]
    public async Task LanguageTransactionsKeepNativeIdentityAndPublishOneLanguageRevision(string platform)
    {
        var path = Example(platform == "ftview-me" ? "FactoryTalkLanguagesDemo.xls" : "ViewDesignerV9LanguagesDemo.xlsx"); var p = await service.RegisterHmiLanguageAsync(new(path, platform));
        var page = service.BrowseHmiLanguage(p.Id, new(p.CurrentSha256)); var key = page.Records[0].Key; var t = await service.BeginTransactionAsync(new(p.Id, "hmi-languages", p.CurrentSha256));
        t = await service.StageTransactionAsync(t.Id, new(0, LanguageEdits: [new(key, "fr-FR", "Prêt")], AddLanguages: ["fr-FR"]));
        if (platform == "view-designer") await Assert.ThrowsAsync<OperationException>(() => service.StageTransactionAsync(t.Id, new(t.Version, LanguageEdits: [new(key, "en-US", "Cannot change a source key")])));
        Assert.Equal(0, service.HmiLanguage(p.Id).Revision); var preview = await service.PreviewTransactionAsync(t.Id, new(t.Version)); await service.CommitTransactionAsync(t.Id, new(t.Version, preview.ReviewSha256));
        var after = service.HmiLanguage(p.Id); Assert.Equal(1, after.Revision); Assert.Equal("Prêt", service.BrowseHmiLanguage(after.Id, new(after.CurrentSha256)).Records[0].Texts.Single(x => x.Language == "fr-FR").Text); Assert.Equal(p.SourceSha256, ProjectService.Hash(File.ReadAllBytes(path)));
    }
    [Fact]
    public async Task ViewDesignerPropertyTransactionPreservesAssetsAndOriginalExport()
    {
        var source = Path.Combine(root, "export"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "ViewApplication.hmi"), "ViewProject Demo { HomeScreen := \"Home\"; }");
        const string original = "Screen Home { Button Start { Text := \"Ready\"; } }";
        File.WriteAllText(Path.Combine(source, "Home.hmi"), original); File.WriteAllBytes(Path.Combine(source, "asset.bin"), [1, 2, 3]);
        var p = await service.RegisterViewDesignerAsync(source); var t = await service.BeginTransactionAsync(new(p.Id, "view-designer", p.CurrentSha256));
        t = await service.StageTransactionAsync(t.Id, new(0, ViewDesignerEdits: [new("Home.hmi", "/Screen:Home/Button:Start/@Text", "\"Transaction text\"")]));
        Assert.Equal(0, service.ViewDesignerProject(p.Id).Revision); var preview = await service.PreviewTransactionAsync(t.Id, new(t.Version)); await service.CommitTransactionAsync(t.Id, new(t.Version, preview.ReviewSha256));
        var directory = Path.Combine(options.StateDirectory, "viewdesigner", p.Id, "revision-1"); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "asset.bin")));
        Assert.Contains("Transaction text", File.ReadAllText(Path.Combine(directory, "Home.hmi"))); Assert.Equal(original, File.ReadAllText(Path.Combine(source, "Home.hmi")));
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
