using System.Text.Json;
using Studio5000.Core;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class LegacyStatelessRetirementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-legacy-intent-" + Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(root, "old");
    private string Backend => Path.Combine(root, "backend");
    private readonly Driver driver = new();
    private readonly ResourceLedger ledger;
    private readonly ResourceRecord intent;
    private readonly ResourceLedger.ResourceOwner actor;
    private OfflineFrontendPin proof;
    private OfflineServicePin pin;
    private static ProcessIdentity Identity(uint pid, string file) => new ProcessIdentity(new('a', 64), pid, "123", file).Normalize();
    private sealed class Driver : IResourceProcessDriver
    {
        internal ProcessIdentity Actor = null!;
        internal readonly Dictionary<ProcessIdentity, string> States = [];
        internal readonly Dictionary<uint, ProcessIdentity> Captures = [];
        public ProcessIdentity CurrentIdentity() => Actor;
        public ProcessIdentity Capture(uint pid) => Captures[pid];
        public string State(ProcessIdentity identity) => identity == Actor ? "alive" : States.GetValueOrDefault(identity, "absent");
        public IResourceProcessHandle Open(ProcessIdentity identity) => throw new InvalidOperationException("Retirement may never open a termination handle.");
    }
    public LegacyStatelessRetirementTests()
    {
        Directory.CreateDirectory(root); Directory.CreateDirectory(Old); Directory.CreateDirectory(Backend);
        var runtime = new[] { OfflineServicePin.Service, "artifacts/app/Service/Studio5000.Service.dll", "artifacts/app/Service/Studio5000.Core.dll",
            OfflineServicePin.Worker, "artifacts/app/ExtensionWorker/Studio5000.ExtensionWorker.dll", "artifacts/app/ExtensionWorker/Studio5000.Core.dll", OfflineServicePin.Script };
        foreach (var path in runtime) Write(Path.Combine(root, path));
        var names = new[] { "Studio5000.StdioHost.exe", "Studio5000.StdioHost.dll", "Studio5000.StdioHost.deps.json", "Studio5000.StdioHost.runtimeconfig.json",
            "Studio5000.Mcp.exe", "Studio5000.Mcp.dll", "Studio5000.Mcp.deps.json", "Studio5000.Mcp.runtimeconfig.json", "Studio5000.Core.dll" };
        foreach (var name in names) Write(Path.Combine(Old, name));
        var files = names.Select(n => Entry(Old, n)).ToArray();
        File.WriteAllText(Path.Combine(Old, "backend.json"), JsonSerializer.Serialize(new { schema_version = 1, contract = "rockwell-owned-stdio-v1",
            source_commit = OfflineServicePin.LegacyUnboundSource, files }, OfflineServicePin.Json));
        proof = new(Old, files, OfflineServicePin.LegacyUnboundContract, OfflineServicePin.LegacyUnboundSource, OfflineServicePin.Hash(Path.Combine(Old, "backend.json")));
        pin = new(1, "26.1022.174219", runtime.Select(n => Entry(root, n)).ToArray(), [proof]); SavePin();
        File.WriteAllText(Path.Combine(root, "service.local.json"), JsonSerializer.Serialize(new { ServiceUrl = "http://127.0.0.1:5487",
            Studio5000 = new { EnableControllerOperations = false, EnableSdkServiceRecovery = false, AutoStartSdkService = false,
                NativeBridgePath = (string?)null, CipBridgePath = (string?)null, ExtensionWorkerPath = OfflineServicePin.Worker,
                MaximumOwnedResources = 16, MaximumMcpClients = 4 } }));
        ledger = new(Path.Combine(root, "registry"), driver, 16);
        driver.Actor = Identity(50, Path.Combine(Old, "Studio5000.StdioHost.exe"));
        intent = ledger.Owner("rockwell-mcp-server").BeginLaunch("Rockwell MCP stdio adapter", "mcp-adapter", cleanupMode: "terminate");
        driver.Actor = Identity(42, Path.Combine(root, "current", "Studio5000.StdioHost.exe"));
        actor = ledger.Owner("rockwell-mcp-server");
    }
    private static void Write(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "Generated harmless provenance fixture"); }
    private static OfflinePinnedFile Entry(string directory, string path) => new(path, new FileInfo(Path.Combine(directory, path)).Length, OfflineServicePin.Hash(Path.Combine(directory, path)));
    private void SavePin() => File.WriteAllText(Path.Combine(Backend, OfflineServicePin.FileName), JsonSerializer.Serialize(pin, OfflineServicePin.Json));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [InlineData("absent")]
    [InlineData("different")]
    public void Reviewed_legacy_contract_retires_only_departed_stateless_intent_without_inventing_a_pid(string state)
    {
        if (!OperatingSystem.IsWindows()) return;
        driver.States[intent.Owner] = state;
        OfflineServiceBootstrap.PrepareAdmission(root, Backend, actor);
        var result = ledger.Get(intent.ResourceId);
        Assert.Equal("released", result.State); Assert.Null(result.Process); Assert.Empty(result.Members);
        Assert.Equal(intent.Owner, result.Owner); Assert.Equal(intent.Revision, result.Reconciliation!.PreviousRevision);
        Assert.Equal(actor.Identity, result.Reconciliation.Actor); Assert.Contains(OfflineServicePin.ProofHash(proof), result.Reconciliation.Evidence);
    }
    [Theory]
    [InlineData("live")]
    [InlineData("unknown")]
    [InlineData("no-proof")]
    [InlineData("native-kind")]
    [InlineData("label")]
    [InlineData("barrier")]
    [InlineData("shared")]
    [InlineData("members")]
    [InlineData("conflict")]
    public void Unproved_unknown_native_or_conflicting_records_remain_byte_identical(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        var record = intent;
        if (failure is "live" or "unknown") driver.States[intent.Owner] = failure == "live" ? "alive" : "unknown";
        if (failure == "no-proof") { pin = pin with { PreviousFrontends = [proof with { UnboundLaunchContract = null }] }; SavePin(); }
        if (failure == "native-kind") record = record with { Kind = "sdk-worker" };
        if (failure == "label") record = record with { Label = "Unreviewed use of this owner" };
        if (failure == "barrier") record = record with { RecoveryBarrier = "Native uncertainty must remain" };
        if (failure == "shared") record = record with { Ownership = "shared", CleanupMode = "report" };
        if (failure == "members") record = record with { Members = [Identity(300, Path.Combine(root, "user-owned.exe"))] };
        var path = Path.Combine(ledger.DirectoryPath, record.ResourceId + ".json"); File.WriteAllBytes(path, ResourceJson.Write(record));
        if (failure == "conflict")
        {
            var shared = record with { ResourceId = Guid.NewGuid().ToString("N"), AppId = "another-app", Owner = Identity(700, Path.Combine(root, "other.exe")),
                State = "active", Process = record.Owner, Ownership = "shared", CleanupMode = "report" };
            File.WriteAllBytes(Path.Combine(ledger.DirectoryPath, shared.ResourceId + ".json"), ResourceJson.Write(shared));
        }
        var bytes = File.ReadAllBytes(path); OfflineServiceBootstrap.PrepareAdmission(root, Backend, actor); Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void A_stale_reviewed_revision_cannot_retire_an_otherwise_eligible_claim()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(ledger.DirectoryPath, intent.ResourceId + ".json");
        var bytes = ResourceJson.Write(intent with { UpdatedAt = intent.UpdatedAt + 1 }); File.WriteAllBytes(path, bytes);
        Assert.Null(actor.RetireUnboundStatelessFrontend(intent.ResourceId, intent.Revision, Path.Combine(Old, "Studio5000.StdioHost.exe"), OfflineServicePin.ProofHash(proof)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void Another_current_actor_cannot_impersonate_the_recorded_reconciliation_actor()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(ledger.DirectoryPath, intent.ResourceId + ".json"); var bytes = File.ReadAllBytes(path);
        driver.Actor = Identity(900, Path.Combine(root, "different-owner.exe"));
        Assert.Null(actor.RetireUnboundStatelessFrontend(intent.ResourceId, intent.Revision, Path.Combine(Old, "Studio5000.StdioHost.exe"), OfflineServicePin.ProofHash(proof)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Theory]
    [InlineData("source")]
    [InlineData("manifest")]
    [InlineData("code")]
    public void Unreviewed_source_or_drift_is_refused_before_any_retirement(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (failure == "source") { pin = pin with { PreviousFrontends = [proof with { SourceCommit = new('1', 40) }] }; SavePin(); }
        if (failure == "manifest") File.AppendAllText(Path.Combine(Old, "backend.json"), " ");
        if (failure == "code") File.AppendAllText(Path.Combine(Old, "Studio5000.Core.dll"), "drift");
        var path = Path.Combine(ledger.DirectoryPath, intent.ResourceId + ".json"); var bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => OfflineServiceBootstrap.PrepareAdmission(root, Backend, actor)); Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void Real_shape_pool_accounting_preserves_eight_unproved_records_and_all_live_peers()
    {
        if (!OperatingSystem.IsWindows()) return;
        for (var n = 0; n < 8; n++)
        {
            driver.Actor = Identity((uint)(70 + n), Path.Combine(root, "unreviewed", "Studio5000.StdioHost.exe"));
            ledger.Owner("rockwell-mcp-server").BeginLaunch("Rockwell MCP stdio adapter", "mcp-adapter", cleanupMode: "terminate");
        }
        driver.Actor = Identity(100, Path.Combine(root, OfflineServicePin.Service)); driver.States[driver.Actor] = "alive";
        for (var n = 0; n < 4; n++)
        {
            var owner = ledger.Owner("rockwell-mcp-server"); driver.Captures[(uint)(200+n)] = Identity((uint)(200+n), Path.Combine(root, OfflineServicePin.Worker));
            var before = owner.BeginLaunch("Live worker", "extension-worker", cleanupMode: "terminate"); var bound = owner.BindProcess(before.ResourceId, (uint)(200+n), "windows_job"); driver.States[bound.Process!] = "alive";
        }
        for (var n = 0; n < 3; n++)
        {
            driver.Actor = Identity((uint)(500+n), Path.Combine(Old, "Studio5000.StdioHost.exe"));
            var owner = ledger.Owner("rockwell-mcp-server"); driver.Captures[(uint)(600+n)] = Identity((uint)(600+n), Path.Combine(Old, "Studio5000.Mcp.exe"));
            var before = owner.BeginLaunch("Rockwell MCP stdio adapter", "mcp-adapter", cleanupMode: "terminate"); var bound = owner.BindProcess(before.ResourceId, (uint)(600+n), "windows_job");
            if (n < 2) { driver.States[bound.Owner] = "alive"; driver.States[bound.Process!] = "alive"; }
        }
        driver.Actor = actor.Identity;
        var files = Directory.GetFiles(ledger.DirectoryPath, "*.json").ToDictionary(p => p, File.ReadAllBytes);
        var preview = JsonSerializer.SerializeToElement(OfflineServiceBootstrap.PreviewRecovery(root, Backend, actor), OfflineServicePin.Json);
        Assert.Equal(16, preview.GetProperty("nonterminalClaims").GetInt32()); Assert.Equal(9, preview.GetProperty("currentUnbound").GetInt32());
        Assert.Equal(1, preview.GetProperty("eligibleUnbound").GetInt32()); Assert.Equal(1, preview.GetProperty("eligibleBound").GetInt32());
        Assert.Equal(4, preview.GetProperty("supportedTotalLiveFrontendsByResourceBound").GetInt32());
        Assert.Equal(2, preview.GetProperty("freeResourceClaimsAfterRecovery").GetInt32());
        Assert.True(preview.GetProperty("freshFrontendFitsResourceAndClientBounds").GetBoolean());
        Assert.All(files, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
        OfflineServiceBootstrap.PrepareAdmission(root, Backend, actor);
        Assert.Equal(14, ledger.Records().Count(r => !r.Terminal));
        Assert.Equal(8, ledger.Records().Count(r => !r.Terminal && r.Process is null));
    }
}
