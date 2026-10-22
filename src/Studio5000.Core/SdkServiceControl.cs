using System.Text.Json;
using System.Text.Json.Serialization;
using Studio5000.Core.Resources;

namespace Studio5000.Core;

public sealed record SdkServiceEnsureRequest(string RequestId);
public sealed record SdkServiceSnapshot(string ServiceName, string DisplayName, string State, string HostId, uint ProcessId = 0,
    uint Win32ExitCode = 0, bool Installed = true);
public sealed record SdkServiceStatus(SdkServiceSnapshot Service, bool RecoveryEnabled, bool AutoStartBeforeSdkOperation,
    int StartTimeoutSeconds, string[] UncertainRequestIds, string Ownership = "Windows Service Control Manager");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SdkServiceReceipt(int SchemaVersion, string RequestId, string HostId, string ServiceName, string Source,
    string Outcome, bool StartDispatched, SdkServiceSnapshot? Before, SdkServiceSnapshot? After,
    string Code, string Message, DateTimeOffset RecordedAt);
public interface ISdkServiceDriver
{
    SdkServiceSnapshot Read();
    void Start();
}
public sealed class SdkServiceApiException(int error, bool knownRejected) : Exception("Windows SDK service operation failed.")
{
    public int NativeError { get; } = error;
    public bool KnownRejected { get; } = knownRejected;
}

// Fixed local SDK service only. Starts go through SCM, not a process/job launcher.
// A running service is never stopped, and an uncertain start is never replayed.
public sealed class SdkServiceControl(ServiceOptions options, string directory, ISdkServiceDriver driver, Func<bool> maintenanceHeld)
{
    public const string ServiceName = "LdSdkService", DisplayName = "Logix Designer SDK Service";
    private readonly SemaphoreSlim gate = new(1, 1);
    private int active;
    public int ActiveRequests => Volatile.Read(ref active);
    public SdkServiceStatus Status()
    {
        var snapshot = ReadChecked();
        return new(snapshot, options.EnableSdkServiceRecovery, options.AutoStartSdkService, options.SdkServiceStartTimeoutSeconds,
            Records().Where(r => r.Outcome is "dispatching" or "uncertain").Select(r => r.RequestId).ToArray());
    }
    public async Task EnsureForSdkAsync(CancellationToken ct)
    {
        if (!options.AutoStartSdkService) return;
        var result = await EnsureRunningAsync(new(Guid.NewGuid().ToString("N")), "sdk-preflight", ct);
        if (result.Outcome is not ("already_running" or "started")) throw new OperationException(result.Code, result.Message + " Inspect the service/start receipt before retrying; no SDK request was submitted.");
    }
    public async Task<SdkServiceReceipt> EnsureRunningAsync(SdkServiceEnsureRequest request, string source = "explicit", CancellationToken ct = default)
    {
        if (request.RequestId is null || request.RequestId.Length != 32 || request.RequestId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Supply a lowercase 32-hex requestId and retain it after a lost response.");
        if (source is not ("explicit" or "sdk-preflight")) throw new ArgumentException("Unknown SDK service request source.");
        await gate.WaitAsync(ct); Interlocked.Increment(ref active);
        try
        {
            // Observing the same receipt is always safe, including under hold.
            var retained = Records();
            var previous = retained.SingleOrDefault(r => r.RequestId == request.RequestId);
            if (previous is not null)
            {
                if (previous.Source != source) throw new OperationException("sdk_service_request_conflict", "Request ID belongs to another SDK service request source.");
                return previous;
            }
            if (maintenanceHeld()) throw new OperationException("maintenance_hold", "SDK service recovery is blocked by the operator maintenance hold.");
            ct.ThrowIfCancellationRequested();
            if (options.SdkServiceStartTimeoutSeconds is < 1 or > 120) throw new ArgumentException("SDK service readback timeout must be 1..120 seconds.");
            var before = ReadChecked();
            SdkServiceReceipt Result(string outcome, bool dispatched, SdkServiceSnapshot? after, string code, string message) =>
                new(1, request.RequestId, before.HostId, ServiceName, source, outcome, dispatched, before, after, code, message, DateTimeOffset.UtcNow);
            SdkServiceReceipt Save(SdkServiceReceipt value) { Persist(value); return value; }
            if (!before.Installed) return Save(Result("refused", false, before, "sdk_service_missing", "Logix Designer SDK Service is not installed."));
            if (before.State == "Running") return Save(Result("already_running", false, before, "ok", "SDK service is already running; no service control was sent."));
            if (before.State == "StartPending")
            {
                var observe = Result("dispatching", false, null, "sdk_service_start_pending", "Observing an existing startup; no start control was sent.");
                Persist(observe);
                try { return Save(await WaitForRunningAsync(observe, ct)); }
                catch (Exception) { return Save(observe with { Outcome = "uncertain", Code = "sdk_service_observation_uncertain", Message = "Existing SDK startup could not be observed to completion. This request sent no start control." }); }
            }
            if (before.State != "Stopped") return Save(Result("refused", false, before, "sdk_service_transition", "SDK service is " + before.State + "; inspect its transition. Only a stopped service can be started."));
            if (!options.EnableSdkServiceRecovery) return Save(Result("refused", false, before, "sdk_service_recovery_disabled", "Enable SDK service recovery in Settings before starting the stopped service."));
            if (retained.Any(r => r.StartDispatched && (r.Outcome is "dispatching" or "uncertain")))
                throw new OperationException("sdk_service_recovery_uncertain", "An earlier SDK service start is unresolved. Inspect its retained request; no replacement start was sent.");
            var intent = Result("dispatching", true, null, "sdk_service_start_dispatched", "Start intent recorded before the Windows SCM request.");
            ct.ThrowIfCancellationRequested(); Persist(intent);
            try
            {
                ct.ThrowIfCancellationRequested();
                driver.Start();
                return Save(await WaitForRunningAsync(intent, ct));
            }
            catch (SdkServiceApiException ex) when (ex.KnownRejected)
            {
                return Save(intent with { Outcome = "failed", Code = ex.NativeError == 5 ? "sdk_service_access_denied" : ex.NativeError == 1058 ? "sdk_service_disabled" : "sdk_service_start_rejected",
                    Message = ex.NativeError == 5 ? "Windows denied SERVICE_START permission. Run the shared Rockwell service with an account permitted to start LdSdkService; no elevation or permissions change was attempted."
                        : ex.NativeError == 1058 ? "Windows reports the SDK service disabled. Review its Windows configuration; the app does not change startup type." : "Windows rejected SDK service start (error " + ex.NativeError + ")." });
            }
            catch (Exception)
            {
                return Save(intent with { Outcome = "uncertain", Code = "sdk_service_start_uncertain", Message = "SDK service start outcome is unknown. Inspect this receipt and Windows service status; do not replace or replay the start." });
            }
        }
        finally { Interlocked.Decrement(ref active); gate.Release(); }
    }
    private async Task<SdkServiceReceipt> WaitForRunningAsync(SdkServiceReceipt intent, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); var current = ReadChecked();
            if (current.HostId != intent.HostId) throw new InvalidOperationException("SCM host identity changed.");
            if (current.State == "Running") return intent with { Outcome = intent.StartDispatched ? "started" : "already_running", After = current, Code = "ok", Message = "Windows reports the SDK service running. Native project/connection readiness is not implied." };
            if (current.State == "Stopped") return intent with { Outcome = "failed", After = current, Code = "sdk_service_start_failed", Message = "SDK service returned to Stopped (Windows exit code " + current.Win32ExitCode + "). Inspect Windows logs; no retry was sent." };
            if (started.Elapsed >= TimeSpan.FromSeconds(options.SdkServiceStartTimeoutSeconds)) return intent with { Outcome = "uncertain", After = current, Code = "sdk_service_start_timeout", Message = "SDK service did not reach Running before the timeout. Inspect its current state; no retry was sent." };
            await Task.Delay(100, ct);
        }
    }
    private SdkServiceSnapshot ReadChecked()
    {
        try { var value = driver.Read(); ValidateIdentity(value); return value; }
        catch (SdkServiceApiException ex)
        {
            throw new OperationException(ex.NativeError == 5 ? "sdk_service_access_denied" : "sdk_service_query_failed",
                "Cannot query local LdSdkService (Windows error " + ex.NativeError + "). Check SDK installation and this account's service-query permission.");
        }
    }
    private static void ValidateIdentity(SdkServiceSnapshot value)
    {
        if (value.ServiceName != ServiceName || value.DisplayName != DisplayName || value.HostId.Length != 64 || value.HostId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new OperationException("sdk_service_identity_mismatch", "Windows SDK service identity differs from the fixed local LdSdkService target.");
    }
    private SdkServiceReceipt[] Records()
    {
        ProjectService.RejectLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var paths = Directory.EnumerateFiles(directory, "*.json").Take(4097).ToArray();
        if (paths.Length > 4096) throw new IOException("SDK service receipt history is full; preserve evidence before archival.");
        return paths.Select(path =>
        {
            ProjectService.RejectLinks(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[16385]; var count = 0;
            while (count < bytes.Length)
            {
                var read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > 16384) throw new IOException("Invalid SDK service receipt size.");
            var value = JsonSerializer.Deserialize<SdkServiceReceipt>(bytes.AsSpan(0, count), ProjectService.Json) ?? throw new IOException("Invalid SDK service receipt.");
            if (value.SchemaVersion != 1 || value.ServiceName != ServiceName || Path.GetFileNameWithoutExtension(path) != value.RequestId || value.Outcome is not ("dispatching" or "uncertain" or "started" or "already_running" or "failed" or "refused")) throw new IOException("Invalid retained SDK service identity/outcome.");
            if (value.Source is not ("explicit" or "sdk-preflight") || value.Before is null || value.HostId != value.Before.HostId) throw new IOException("Invalid retained SDK service source/host.");
            ValidateIdentity(value.Before);
            if (value.After is not null) { ValidateIdentity(value.After); if (value.After.HostId != value.HostId) throw new IOException("Invalid retained SDK service readback host."); }
            return value;
        }).ToArray();
    }
    private void Persist(SdkServiceReceipt value)
    {
        ProjectService.RejectLinks(directory); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, value.RequestId + ".json");
        if (!File.Exists(path) && Directory.EnumerateFiles(directory, "*.json").Take(4096).Count() >= 4096) throw new IOException("SDK service receipt history is full.");
        NativeLifecycleJournal.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, ProjectService.Json));
    }
}
