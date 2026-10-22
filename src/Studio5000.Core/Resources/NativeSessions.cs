using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio5000.Core.Resources;

// A provider/session identity is an opaque connection identity. It is not an
// invented PID for the vendor engine, COM server, WMI provider or Windows service.
public sealed record NativeSoftwareIdentity(string Provider, string InstanceId, string IdentityKind, ProcessIdentity? Process = null)
{
    public NativeSoftwareIdentity Validate()
    {
        ResourceRules.Name(Provider); ResourceRules.Text(InstanceId, 1, 256);
        ResourceRules.Choice(IdentityKind, "sdk-session", "com-instance", "wmi-instance", "native-process");
        if (InstanceId.Any(char.IsControl)) throw new ArgumentException("Native software identity contains control characters.");
        if (IdentityKind == "native-process" && Process is null) throw new ArgumentException("A native process attachment needs its complete explicit identity.");
        return this with { Process = Process?.Normalize() };
    }
}
public sealed record NativeCloseResult(string Outcome, bool DocumentsSafe, bool EffectsKnown, string Evidence)
{
    public static NativeCloseResult Closed(string evidence) => new("closed", true, true, evidence);
    public static NativeCloseResult Refused(string evidence) => new("refused", false, true, evidence);
    public static NativeCloseResult Uncertain(string evidence) => new("uncertain", false, false, evidence);
}
public sealed record NativeSessionState(int SchemaVersion, string ResourceId, string OwnerId, NativeSoftwareIdentity Software,
    string State, int References, string? CloseRequestId, NativeCloseResult? CloseResult);

// Shared service policy, independent of MCP and GUI. Closing a lease only closes
// this app's connection/document adapter. Even the last lease never authorizes
// terminating shared or user-owned software.
public sealed class NativeSessionRegistry(ResourceLedger.ResourceOwner owner, string directory)
{
    private readonly object sync = new();
    private readonly Dictionary<NativeSoftwareIdentity, NativeSessionLease> sessions = [];
    public NativeSessionLease Attach(NativeSoftwareIdentity software, string ownership = "shared")
    {
        software = software.Validate(); ResourceRules.Choice(ownership, "attached", "shared");
        lock (sync)
        {
            if (sessions.TryGetValue(software, out var existing))
            {
                if (existing.State.State == "closed") throw new InvalidOperationException("A closed native identity cannot be adopted as a new connection; supply the new provider instance identity.");
                existing.AddReference(); return existing;
            }
            if (software.Process is not null && owner.Ledger.Driver.State(software.Process) != "alive")
                throw new InvalidOperationException("Explicit native identity is absent, changed or unverifiable; no process name/PID adoption is allowed.");
            var record = owner.BeginLaunch(software.Provider + " " + software.IdentityKind, "native-session", ownership, "report");
            record = software.Process is null ? owner.ActivateOpaqueAttachment(record.ResourceId) : owner.BindProcess(record.ResourceId, software.Process.Pid);
            if (software.Process is not null && record.Process != software.Process) throw new InvalidOperationException("Native identity changed during attachment; retain the untrusted claim for inspection.");
            owner.SetRecoveryBarrier(record.ResourceId, "Native connection/document state requires an app-owned close or recovery attestation.");
            var lease = new NativeSessionLease(owner, directory, record.ResourceId, software);
            sessions.Add(software, lease); return lease;
        }
    }
}

public sealed class NativeSessionLease
{
    private readonly ResourceLedger.ResourceOwner owner;
    private readonly string directory;
    private readonly object sync = new();
    public NativeSessionState State { get; private set; }
    public string ResourceId => State.ResourceId;
    internal NativeSessionLease(ResourceLedger.ResourceOwner owner, string directory, string id, NativeSoftwareIdentity software)
    {
        this.owner = owner; this.directory = Path.GetFullPath(directory);
        State = new(1, id, owner.OwnerId, software, "open", 1, null, null); Persist(State);
    }
    internal void AddReference()
    {
        lock (sync)
        {
            if (State.State != "open" || State.References >= 256) throw new InvalidOperationException("Native lease admission is closed or full.");
            Persist(State with { References = State.References + 1 });
        }
    }
    public NativeCloseResult Close(Func<NativeCloseResult> closeConnection)
    {
        lock (sync)
        {
            if (State.State != "open") throw new InvalidOperationException("Native close already dispatched; inspect retained evidence instead of replaying it.");
            if (State.References > 1)
            {
                Persist(State with { References = State.References - 1 });
                return new("retained_shared", false, true, "Other local leases still use this app-owned connection.");
            }
            // Flush before entering provider code. A restart never resumes this intent.
            Persist(State with { State = "closing", CloseRequestId = Guid.NewGuid().ToString("N"), CloseResult = null });
            NativeCloseResult result;
            try
            {
                result = closeConnection();
                ResourceRules.Choice(result.Outcome, "closed", "refused", "uncertain"); ResourceRules.Text(result.Evidence, 1, 512);
                if (result.Outcome == "closed" && (!result.DocumentsSafe || !result.EffectsKnown)) result = NativeCloseResult.Uncertain("Provider did not attest saved/discardable documents and known effects.");
                if (result.Outcome == "refused" && !result.EffectsKnown) result = NativeCloseResult.Uncertain("Provider could not attest a zero-effect refusal.");
            }
            catch (Exception) { result = NativeCloseResult.Uncertain("Native close was dispatched without a known result; inspect the retained session and original operation journal."); }
            Persist(State with { State = result.Outcome == "refused" ? "refused" : result.Outcome, CloseResult = result });
            if (result.Outcome == "closed")
            {
                var record = owner.Ledger.Get(ResourceId);
                if (record.RecoveryBarrier is not null) owner.ResolveRecoveryBarrier(ResourceId, record.Revision, result.Evidence);
                owner.ReleaseAttachment(ResourceId);
                Persist(State with { References = 0 });
            }
            return result;
        }
    }
    // Only an explicit caller review may retry a known zero-effect refusal.
    public void ReviewRefusal(string evidence)
    {
        ResourceRules.Text(evidence, 1, 512);
        lock (sync)
        {
            if (State.State != "refused" || State.CloseResult?.Outcome != "refused") throw new InvalidOperationException("Only a known refusal can be reviewed; uncertain effects require separate recovery.");
            Write("review-" + Guid.NewGuid().ToString("N"), new { State.ResourceId, State.CloseRequestId, State.CloseResult, evidence });
            Persist(State with { State = "open" });
        }
    }
    private void Persist(NativeSessionState state) { Write(state.ResourceId, state); State = state; }
    private void Write(string name, object value)
    {
        ProjectService.RejectLinks(directory); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".json"); ProjectService.RejectLinks(path);
        if (!File.Exists(path) && Directory.EnumerateFiles(directory, "*.json").Take(4096).Count() >= 4096) throw new IOException("Native session history is full; preserve evidence before archival.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProjectService.Json);
        if (bytes.Length > 32768) throw new IOException("Native session record exceeds its bound.");
        NativeLifecycleJournal.WriteAtomic(path, bytes);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeLifecycleReceipt(int SchemaVersion, string SessionId, ProcessIdentity Worker,
    string Provider, string Phase, bool DocumentsSafe, bool EffectsKnown, bool ConnectionClosed, int? PendingStep, string? PendingMethod);

// Called inside the owned worker before entering native code. No arguments,
// passwords, controller addresses or user document paths enter this journal.
public sealed class NativeLifecycleJournal : IDisposable
{
    private readonly string path;
    private NativeLifecycleReceipt receipt;
    public NativeLifecycleJournal(string path, string sessionId, ProcessIdentity worker, string provider)
    {
        ResourceRules.Token(sessionId, 32); ResourceRules.Name(provider);
        this.path = Path.GetFullPath(path); ProjectService.RejectLinks(this.path);
        receipt = new(1, sessionId, worker.Normalize(), provider, "opening", false, false, false, null, null); Save();
    }
    public static NativeLifecycleJournal? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("ROCKWELL_NATIVE_LIFECYCLE");
        var session = Environment.GetEnvironmentVariable("ROCKWELL_NATIVE_SESSION");
        if (path is null && session is null) return null; // Explicit standalone vendor CLI; no service ownership is inferred.
        if (path is null || session is null) throw new InvalidOperationException("Incomplete native lifecycle handshake.");
        return new(path, session, new WindowsResourceProcessDriver().CurrentIdentity(), "rockwell-logix-sdk");
    }
    public void BeforeStep(int index, string method)
    {
        if (index is < 0 or > 100) throw new ArgumentException("Native checkpoint index exceeds the SDK batch bound.");
        ResourceRules.Text(method, 1, 128);
        receipt = receipt with { Phase = "executing", EffectsKnown = false, PendingStep = index, PendingMethod = method }; Save();
    }
    public void AfterStep()
    {
        if (receipt.PendingStep is null) throw new InvalidOperationException("No dispatched native step to checkpoint.");
        var completed = receipt with { Phase = "step_completed", EffectsKnown = true };
        WriteAtomic(path + "." + receipt.PendingStep.Value.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".completed.json", JsonSerializer.SerializeToUtf8Bytes(completed, ProjectService.Json));
        receipt = receipt with { EffectsKnown = true, PendingStep = null, PendingMethod = null }; Save();
    }
    public void Complete(bool documentsSafe, bool effectsKnown, bool connectionClosed)
    {
        receipt = receipt with { Phase = "closed", DocumentsSafe = documentsSafe, EffectsKnown = effectsKnown, ConnectionClosed = connectionClosed }; Save();
    }
    public static NativeCloseResult VerifyClose(string path, string sessionId, ProcessIdentity worker, bool successfulExit)
    {
        ProjectService.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[32769]; var length = 0;
        while (length < bytes.Length && input.Read(bytes, length, bytes.Length - length) is var count && count != 0) length += count;
        if (length == bytes.Length) throw new IOException("Native close receipt exceeds its limit.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count()) throw new IOException("Ambiguous native receipt fields.");
        var value = JsonSerializer.Deserialize<NativeLifecycleReceipt>(bytes.AsSpan(0, length), ProjectService.Json) ?? throw new IOException("Missing native close receipt.");
        return successfulExit && value.SchemaVersion == 1 && value.SessionId == sessionId && value.Worker.Normalize() == worker &&
            value.Provider == "rockwell-logix-sdk" && value.Phase == "closed" && value.DocumentsSafe && value.EffectsKnown && value.ConnectionClosed && value.PendingStep is null && value.PendingMethod is null
            ? NativeCloseResult.Closed("Exact owned SDK worker attested document preservation/discard policy and closed connections; original operation results remain authoritative.")
            : NativeCloseResult.Uncertain("Native close/save/effect evidence is incomplete or differs from the exact worker/session; inspect without replay.");
    }
    private void Save() => WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(receipt, ProjectService.Json));
    internal static void WriteAtomic(string path, byte[] bytes)
    {
        ProjectService.RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { /* An incomplete receipt remains incomplete. No implicit close or save. */ }
}
