using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Studio5000.Core.Resources;

namespace Studio5000.Core;

public sealed record OfflineServiceReady(bool Started, string Version, ProcessIdentity Owner);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OfflineServiceOwner(int SchemaVersion, string OwnerId, ProcessIdentity Owner, string Registry);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OfflineBootstrapAttempt(string Id, string Phase, ProcessIdentity Actor, ProcessIdentity? Starter,
    OfflineServiceOwner? Service, string SettingsSha256, string PinSha256, string? Diagnostic, ProcessIdentity? Observer = null)
{ public int SchemaVersion { get; init; } = 1; }
internal sealed record OfflineServiceHealth(string Status, string Version);
internal interface IOfflineServiceStarter
{
    Task<OfflineServiceHealth?> ProbeAsync(Uri url, CancellationToken ct);
    Task VerifyEffectivePolicyAsync(string root, OfflineBootstrapSettings settings, CancellationToken ct);
    bool HasUnregisteredServiceProcess(string expectedExecutable);
    Task<int> StartAsync(string root, string output, Action<ProcessIdentity> started, CancellationToken ct);
}

/// <summary>Offline lifecycle only. No engineering request or native service action is dispatched here.</summary>
public static class OfflineServiceBootstrap
{
    public static object PreviewRecovery(string root, string backend, ResourceLedger.ResourceOwner actor)
    {
        root = Path.GetFullPath(root); Guard(root); _ = OfflineServicePin.Settings(root);
        var pin = OfflineServicePin.Load(root, backend); var limits = McpClientLimits.Read(root);
        var records = actor.Ledger.Records().Where(r => r.AppId == "rockwell-mcp-server" && !r.Terminal).ToArray();
        var plans = records.Select(record =>
        {
            var ownerState = actor.Ledger.Driver.State(record.Owner);
            var processState = record.Process is null ? "unpublished" : actor.Ledger.Driver.State(record.Process);
            var rule = "retain"; string? proof = null;
            if (record.Kind == "extension-worker" && actor.CanReconcileClosedOfflineWorker(record,
                Path.GetFullPath(Path.Combine(root, OfflineServicePin.Service)), Path.GetFullPath(Path.Combine(root, OfflineServicePin.Worker)))) rule = "closed-offline-worker";
            if (record.Kind == "mcp-adapter")
                foreach (var previous in pin.PreviousFrontends)
                {
                    var host = Path.Combine(previous.Directory, "Studio5000.StdioHost.exe");
                    if (!string.Equals(record.Owner.Executable, host, StringComparison.OrdinalIgnoreCase)) continue;
                    if (record.Process is null && previous.UnboundLaunchContract == OfflineServicePin.LegacyUnboundContract && actor.CanRetireUnboundStatelessFrontend(record, host))
                    { rule = "reviewed-unbound-mcp"; proof = OfflineServicePin.ProofHash(previous); }
                    else if (actor.CanReconcileClosedMcpFrontend(record, Path.Combine(previous.Directory, "Studio5000.Mcp.exe"), host)) rule = "closed-mcp";
                    break;
                }
            var liveFrontend = record.Kind == "mcp-adapter" && record.Process is not null && ownerState == "alive" && processState == "alive";
            return new { resourceId = record.ResourceId, revision = record.Revision, record.Kind, record.State, owner = record.Owner,
                process = record.Process, ownerState, processState, rule, proofSha256 = proof, liveFrontend };
        }).ToArray();
        var eligible = plans.Count(p => p.rule != "retain");
        var protectedNonFrontends = plans.Count(p => p.rule == "retain" && !p.liveFrontend);
        var maximum = Math.Max(0, Math.Min(limits.MaximumInstances, limits.MaximumOwnedResources - protectedNonFrontends));
        return new { readOnly = true, sharedStateChanged = false, nonterminalClaims = records.Length, resourceLimit = limits.MaximumOwnedResources,
            configuredMaximumMcpClients = limits.MaximumInstances, currentUnbound = records.Count(r => r.Process is null),
            eligibleUnbound = plans.Count(p => p.rule == "reviewed-unbound-mcp"), eligibleBound = eligible - plans.Count(p => p.rule == "reviewed-unbound-mcp"),
            protectedNonFrontendClaimsAfterRecovery = protectedNonFrontends, supportedTotalLiveFrontendsByResourceBound = maximum,
            liveFrontends = plans.Count(p => p.liveFrontend), freeResourceClaimsAfterRecovery = limits.MaximumOwnedResources - records.Length + eligible,
            freshFrontendFitsResourceAndClientBounds = limits.MaximumOwnedResources - records.Length + eligible > 0 && plans.Count(p => p.liveFrontend) < limits.MaximumInstances,
            actualTransportAcceptanceVerified = false, plans };
    }
    public static void PrepareAdmission(string root, string backend, ResourceLedger.ResourceOwner actor)
    {
        root = Path.GetFullPath(root); Guard(root);
        var pin = OfflineServicePin.Load(root, backend); _ = OfflineServicePin.Settings(root);
        Recover(root, pin, actor);
    }
    private static void Recover(string root, OfflineServicePin pin, ResourceLedger.ResourceOwner actor)
    {
        foreach (var record in actor.Ledger.Records().Where(r => r.AppId == "rockwell-mcp-server" && !r.Terminal))
        {
            if (record.Kind == "extension-worker") actor.ReconcileClosedOfflineWorker(record.ResourceId, record.Revision,
                Path.GetFullPath(Path.Combine(root, OfflineServicePin.Service)), Path.GetFullPath(Path.Combine(root, OfflineServicePin.Worker)));
            else if (record.Kind == "mcp-adapter")
                foreach (var previous in pin.PreviousFrontends)
                {
                    var host = Path.Combine(previous.Directory, "Studio5000.StdioHost.exe");
                    if (string.Equals(record.Owner.Executable, host, StringComparison.OrdinalIgnoreCase))
                    {
                        if (record.Process is null && previous.UnboundLaunchContract == OfflineServicePin.LegacyUnboundContract)
                            actor.RetireUnboundStatelessFrontend(record.ResourceId, record.Revision, host, OfflineServicePin.ProofHash(previous));
                        else actor.ReconcileClosedMcpFrontend(record.ResourceId, record.Revision, Path.Combine(previous.Directory, "Studio5000.Mcp.exe"), host);
                        break;
                    }
                }
        }
    }
    public static Task<OfflineServiceReady> EnsureAsync(string root, string backend, ResourceLedger.ResourceOwner actor, CancellationToken ct = default)
        => EnsureAsync(root, backend, actor, new WindowsOfflineServiceStarter(), TimeSpan.FromSeconds(45), ct);

    // The MCP child is inside the frontend's kill job. It may inspect readiness
    // but must never launch the shared service into that private containment.
    public static Task<OfflineServiceReady> ObserveAsync(string root, string backend, ResourceLedger.ResourceOwner actor, CancellationToken ct = default)
        => ObserveAsync(root, backend, actor, new WindowsOfflineServiceStarter(), waitForStartup: true, ct);
    public static Task<OfflineServiceReady> ObserveExistingAsync(string root, string backend, ResourceLedger.ResourceOwner actor, CancellationToken ct = default)
        => ObserveAsync(root, backend, actor, new WindowsOfflineServiceStarter(), waitForStartup: false, ct);
    internal static async Task<OfflineServiceReady> ObserveAsync(string root, string backend, ResourceLedger.ResourceOwner actor,
        IOfflineServiceStarter starter, bool waitForStartup, CancellationToken ct)
    {
        root = Path.GetFullPath(root); Guard(root);
        var settings = OfflineServicePin.Settings(root); var pin = OfflineServicePin.Load(root, backend);
        var expected = Path.GetFullPath(Path.Combine(root, OfflineServicePin.Service)).ToLowerInvariant();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(45)); var token = deadline.Token;
        var local = Path.Combine(root, ".local");
        while (true)
        {
            Guard(root); SameSettings(root, settings);
            var owner = ReadOwner(local); var health = await starter.ProbeAsync(settings.Url, token);
            if (owner is not null)
            {
                if (owner.Owner.Executable != expected) throw new InvalidOperationException("Unmatched service image; no request dispatched.");
                var state = actor.Ledger.Driver.State(owner.Owner);
                if (state == "unknown") throw new InvalidOperationException("Service owner cannot be verified; no request dispatched.");
                if (state == "alive" && health?.Status == "ok" && health.Version == pin.ServiceVersion)
                {
                    var attempt = ReadAttempt(Path.Combine(local, "offline-service-start.json"));
                    if (attempt is null || attempt.Phase == "observed")
                    {
                        await starter.VerifyEffectivePolicyAsync(root, settings, token);
                        return new(false, health.Version, owner.Owner);
                    }
                }
            }
            var pending = ReadAttempt(Path.Combine(local, "offline-service-start.json"));
            if (pending?.Phase == "uncertain") throw new InvalidOperationException("Shared-service startup has retained uncertainty; inspect before another dispatch.");
            if (!waitForStartup) throw new ExistingServiceUnavailableException();
            await Task.Delay(100, token);
        }
    }

    internal static async Task<OfflineServiceReady> EnsureAsync(string root, string backend, ResourceLedger.ResourceOwner actor,
        IOfflineServiceStarter starter, TimeSpan budget, CancellationToken ct)
    {
        root = Path.GetFullPath(root); backend = Path.GetFullPath(backend); Guard(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(budget); var token = deadline.Token;
        var local = Path.Combine(root, ".local"); Directory.CreateDirectory(local); ProjectService.RejectLinks(local);
        await using var startupLock = await Acquire(Path.Combine(local, "offline-service-start.lock"), token);
        await using var settingsLock = await Acquire(Path.Combine(local, "settings.lock"), token);
        Guard(root); var settings = OfflineServicePin.Settings(root); var pin = OfflineServicePin.Load(root, backend);
        var pinHash = OfflineServicePin.Hash(Path.Combine(backend, OfflineServicePin.FileName));
        var journalPath = Path.Combine(local, "offline-service-start.json"); var prior = ReadAttempt(journalPath);
        var expected = Path.GetFullPath(Path.Combine(root, OfflineServicePin.Service)).ToLowerInvariant();
        while (true)
        {
            var owner = ReadOwner(local); var health = await starter.ProbeAsync(settings.Url, token);
            var state = owner is null ? "missing" : actor.Ledger.Driver.State(owner.Owner);
            if (owner is not null && owner.Owner.Executable != expected) throw new InvalidOperationException("Saved service owner belongs to another image; automatic startup refused.");
            if (state == "alive" && health?.Status == "ok" && health.Version == pin.ServiceVersion)
            {
                await starter.VerifyEffectivePolicyAsync(root, settings, token);
                SameSettings(root, settings); Guard(root);
                if (prior is not null && prior.Phase != "observed") WriteAttempt(local, prior with { Phase = "observed", Service = owner, Observer = actor.Identity, Diagnostic = "Genuine matching service observed; no startup replay." });
                return new(false, health.Version, owner!.Owner);
            }
            if (state == "unknown" || health is not null && state != "alive") throw new InvalidOperationException("Service listener or owner cannot be matched; no process was adopted or started.");
            if (state == "alive" || starter.HasUnregisteredServiceProcess(expected))
            { await Task.Delay(100, token); continue; } // Actual self-registration may still be in progress.
            if (state is not ("missing" or "absent" or "different")) throw new InvalidOperationException("Service state is not verified absent.");
            break;
        }
        if (prior is not null && prior.Phase != "observed")
            throw new InvalidOperationException("An earlier service startup has an unresolved outcome. Inspect its immutable intent/dispatch evidence; automatic replay is blocked.");
        Recover(root, pin, actor); SameSettings(root, settings); Guard(root);
        var attempt = new OfflineBootstrapAttempt(Guid.NewGuid().ToString("N"), "prepared", actor.Identity, null, null, settings.SettingsSha256, pinHash, null);
        var output = Path.Combine(local, "offline-service-start-history", attempt.Id);
        ArchiveLogs(local, output);
        WriteAttempt(local, attempt);
        try
        {
            var exit = await starter.StartAsync(root, output, identity =>
            {
                attempt = attempt with { Phase = "dispatched", Starter = identity.Normalize() }; WriteAttempt(local, attempt);
            }, token);
            if (exit != 0) throw new InvalidOperationException("The pinned service starter failed; its outcome remains retained for inspection.");
            while (true)
            {
                var owner = ReadOwner(local); var health = await starter.ProbeAsync(settings.Url, token);
                if (owner is not null && owner.Owner.Executable == expected && actor.Ledger.Driver.State(owner.Owner) == "alive" && health?.Status == "ok" && health.Version == pin.ServiceVersion)
                {
                    await starter.VerifyEffectivePolicyAsync(root, settings, token);
                    SameSettings(root, settings); Guard(root); WriteAttempt(local, attempt with { Phase = "observed", Service = owner, Observer = actor.Identity });
                    return new(true, health.Version, owner.Owner);
                }
                await Task.Delay(100, token);
            }
        }
        catch (Exception error)
        {
            WriteAttempt(local, attempt with { Phase = "uncertain", Diagnostic = error is OperationCanceledException ? "Startup readiness deadline/cancellation; no process was terminated and no action may be replayed." : error.Message });
            throw;
        }
    }
    private static void Guard(string root)
    {
        if (File.Exists(Path.Combine(root, ".local", "maintenance-hold.json"))) throw new InvalidOperationException("Maintenance hold blocks offline automatic startup.");
        var update = Path.Combine(root, ".local", "application-update.lock"); ProjectService.RejectLinks(update);
        if (File.Exists(update)) using (new FileStream(update, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
    }
    private static void SameSettings(string root, OfflineBootstrapSettings expected)
    { if (OfflineServicePin.Hash(Path.Combine(root, "service.local.json")) != expected.SettingsSha256) throw new InvalidOperationException("Settings changed during offline startup; retain the actual outcome."); }
    private static void ArchiveLogs(string local, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); ProjectService.RejectLinks(Path.GetDirectoryName(output)!);
        foreach (var name in new[] { "service.stdout.log", "service.stderr.log" })
        {
            var source = Path.Combine(local, name); ProjectService.RejectLinks(source);
            if (!File.Exists(source)) continue;
            if (new FileInfo(source).Length > 33554432) throw new InvalidOperationException("Preserve/compact large previous service logs before automatic startup.");
            File.Copy(source, output + "-previous-" + name, overwrite: false);
        }
    }
    private static async Task<FileStream> Acquire(string path, CancellationToken ct)
    {
        ProjectService.RejectLinks(path);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, ct); }
        }
    }
    private static OfflineServiceOwner? ReadOwner(string local)
    {
        var file = Path.Combine(local, "service-owner.json"); if (!File.Exists(file)) return null;
        using var document = OfflineServicePin.Read(file, 8192);
        var owner = document.Deserialize<OfflineServiceOwner>(OfflineServicePin.Json) ?? throw new InvalidOperationException("Missing actual service owner.");
        ResourceRules.Token(owner.OwnerId, 32);
        if (owner.SchemaVersion != 1) throw new InvalidOperationException("Invalid service owner receipt.");
        return owner with { Owner = owner.Owner.Normalize() };
    }
    private static OfflineBootstrapAttempt? ReadAttempt(string file)
    {
        if (!File.Exists(file)) return null;
        using var document = OfflineServicePin.Read(file, 16384);
        var value = document.Deserialize<OfflineBootstrapAttempt>(OfflineServicePin.Json) ?? throw new InvalidOperationException("Missing retained startup intent.");
        ResourceRules.Token(value.Id, 32); _ = value.Actor.Normalize();
        if (value.SchemaVersion != 1 || value.Phase is not ("prepared" or "dispatched" or "observed" or "uncertain")) throw new InvalidOperationException("Invalid retained startup phase.");
        return value;
    }
    private static void WriteAttempt(string local, OfflineBootstrapAttempt attempt)
    {
        var history = Path.Combine(local, "offline-service-start-history"); Directory.CreateDirectory(history); ProjectService.RejectLinks(history);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(attempt, OfflineServicePin.Json);
        var receipt = Path.Combine(history, attempt.Id + "-" + attempt.Phase + "-" + Guid.NewGuid().ToString("N") + ".json");
        using (var output = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
        NativeLifecycleJournal.WriteAtomic(Path.Combine(local, "offline-service-start.json"), bytes);
    }
}

internal sealed class WindowsOfflineServiceStarter : IOfflineServiceStarter
{
    public async Task VerifyEffectivePolicyAsync(string root, OfflineBootstrapSettings settings, CancellationToken ct)
    {
        var key = Path.Combine(root, ".local", "service.key"); ProjectService.RejectLinks(key);
        if (new FileInfo(key).Length > 4096) throw new InvalidOperationException("Unexpected service credential size.");
        var token = File.ReadAllText(key).Trim();
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.GetAsync(new Uri(settings.Url, "api/settings"), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var body = new MemoryStream();
        var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0) { body.Write(buffer, 0, count); if (body.Length > 2097152) throw new InvalidOperationException("Effective settings exceed their bound."); }
        using var data = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        var revision = data.RootElement.GetProperty("revision").GetString();
        if (revision != settings.SettingsSha256 || OfflineServicePin.ValidateSettings(root, data.RootElement.GetProperty("effective")) != settings.Url)
            throw new InvalidOperationException("The genuine service's effective policy does not match the selected offline configuration.");
    }
    public async Task<OfflineServiceHealth?> ProbeAsync(Uri url, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            using var response = await http.GetAsync(new Uri(url, "health"), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return new("unmatched", "");
            await using var input = await response.Content.ReadAsStreamAsync(ct); using var bounded = new MemoryStream();
            var buffer = new byte[4096]; int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0) { bounded.Write(buffer, 0, count); if (bounded.Length > 65536) throw new InvalidOperationException("Loopback health response exceeds its bound."); }
            using var data = JsonDocument.Parse(bounded.ToArray());
            return new(data.RootElement.GetProperty("status").GetString() ?? "", data.RootElement.GetProperty("version").GetString() ?? "");
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
    public bool HasUnregisteredServiceProcess(string expectedExecutable)
    {
        // Read-only duplicate refusal, never adoption/termination by name.
        var matches = Process.GetProcessesByName("Studio5000.Service");
        try
        {
            var driver = new WindowsResourceProcessDriver();
            foreach (var process in matches)
            {
                try { if (driver.Capture((uint)process.Id).Executable == expectedExecutable) return true; }
                catch (ResourceProcessExitedException) { }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
            }
            return false;
        }
        finally { foreach (var process in matches) process.Dispose(); }
    }
    public async Task<int> StartAsync(string root, string output, Action<ProcessIdentity> started, CancellationToken ct)
    {
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, OfflineServicePin.Script), "-Background" }) start.ArgumentList.Add(argument);
        start.Environment["PSModulePath"] = ""; start.Environment["STUDIO5000_PLUGIN_ROOT"] = root;
        if (start.Environment.TryGetValue("ROCKWELL_MANAGED_SERVICE", out var managed) && managed == "1") throw new InvalidOperationException("A managed/native service route cannot be selected by offline bootstrap.");
        return await WindowsOfflineStarterProcess.RunAsync(start, output, started, ct);
    }
}
