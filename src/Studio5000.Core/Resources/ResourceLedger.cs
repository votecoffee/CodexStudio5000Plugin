using System.Security.AccessControl;
using System.Security.Principal;

namespace Studio5000.Core.Resources;

public interface IResourceProcessHandle : IDisposable
{
    ProcessIdentity Identity { get; }
    bool Alive { get; }
    void Terminate();
    bool Wait(int milliseconds);
}
public interface IResourceProcessDriver
{
    ProcessIdentity CurrentIdentity();
    ProcessIdentity Capture(uint pid);
    string State(ProcessIdentity identity);
    IResourceProcessHandle Open(ProcessIdentity identity);
}
public interface IResourceProcessGroup
{
    ProcessIdentity Identity { get; }
    ProcessIdentity[] Identities();
    void Stop();
}

// Native counterpart of framework 0.20 resources/, including its byte-zero
// ledger.lock and canonical model revision. All mutation stays in the surviving
// owning process; no helper process may impersonate a retained generation.
public sealed class ResourceLedger(string registry, IResourceProcessDriver driver, int maximumActivePerApp = 32, bool nestedServiceOwner = false)
{
    private readonly object mutex = new();
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(registry), "resources");
    internal IResourceProcessDriver Driver => driver;
    internal int MaximumActive => maximumActivePerApp;
    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    private string RecordPath(string id) { ResourceRules.Token(id, 32); return Path.Combine(DirectoryPath, id + ".json"); }
    public ResourceRecord Get(string id)
    {
        var path = RecordPath(id); ProjectService.RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[32769]; var length = 0;
        while (length < bytes.Length && file.Read(bytes, length, bytes.Length - length) is var count && count != 0) length += count;
        var record = ResourceJson.Read(bytes.AsMemory(0, length));
        if (record.ResourceId != id) throw new ArgumentException("Resource identity differs from its filename.");
        return record;
    }
    public ResourceRecord[] Records()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        ProjectService.RejectLinks(DirectoryPath);
        var names = new List<string>();
        foreach (var name in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        { names.Add(name); if (names.Count > 4096) throw new InvalidOperationException("Resource history capacity exceeded; preserve evidence before archival."); }
        return names.Order(StringComparer.Ordinal).Select(name => Get(Path.GetFileNameWithoutExtension(name))).ToArray();
    }
    internal T Locked<T>(Func<T> action)
    {
        if (maximumActivePerApp is < 1 or > 256) throw new ArgumentException("Invalid active resource limit.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Interoperable resource mutation requires the assessed Windows lock profile.");
        lock (mutex)
        {
            PrivateDirectory(Path.GetDirectoryName(DirectoryPath)!); PrivateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "ledger.lock"); ProjectService.RejectLinks(path);
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (file.Length == 0) { file.WriteByte(48); file.Flush(true); }
            WaitForLock(() =>
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                file.Lock(0, 1);
            }, TimeSpan.FromSeconds(5));
            try { return action(); } finally { file.Unlock(0, 1); }
        }
    }
    internal static void WaitForLock(Action acquire, TimeSpan budget)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew(); var delay = 10;
        while (true)
        {
            try { acquire(); return; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && timer.Elapsed < budget)
            { Thread.Sleep(delay); delay = Math.Min(50, delay * 2); }
        }
    }
    private static void PrivateDirectory(string path)
    {
        ProjectService.RejectLinks(path); Directory.CreateDirectory(path); ProjectService.RejectLinks(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var current = WindowsIdentity.GetCurrent();
        var user = current.User ?? throw new InvalidOperationException("Cannot identify the resource registry owner.");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    internal void Save(ResourceRecord record)
    {
        var bytes = ResourceJson.Write(record);
        if (bytes.Length > 32768) throw new ArgumentException("Resource record exceeds 32 KiB.");
        var path = RecordPath(record.ResourceId); var temp = Path.Combine(DirectoryPath, ".resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            if (File.Exists(path)) AtomicMetadataReplacement.Replace(temp, path); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal ResourceRecord Replace(ResourceRecord record)
    { record = (record with { UpdatedAt = Now }).Normalize(); Save(record); return record; }
    public ResourceOwner Owner(string appId)
    { ResourceRules.Name(appId); return new(this, appId, Guid.NewGuid().ToString("N"), driver.CurrentIdentity().Normalize()); }
    internal bool Conflict(ResourceRecord record)
    {
        var identities = record.Members.ToHashSet(); if (record.Process is not null) identities.Add(record.Process);
        return identities.Count != 0 && Records().Any(other => other.ResourceId != record.ResourceId && !other.Terminal &&
            // An app-owned service may retire its own inner job while the
            // Python supervisor retains the outer job. This is delegation only
            // from the exact root process identity, never another attachment,
            // sibling, app, manager or departed service generation.
            !(nestedServiceOwner && other.AppId == record.AppId && other.Process == record.Owner &&
                other.Ownership == "private" && other.CleanupMode == "terminate" && other.Containment == "windows_job" &&
                other.State == "active" && other.Receipt is null) &&
            (other.Process is not null && identities.Contains(other.Process) || other.Members.Any(identities.Contains)));
    }

    public sealed class ResourceOwner
    {
        private readonly ResourceLedger ledger;
        private bool closing;
        public string AppId { get; }
        public string OwnerId { get; }
        public ProcessIdentity Identity { get; }
        internal ResourceLedger Ledger => ledger;
        internal ResourceOwner(ResourceLedger ledger, string appId, string ownerId, ProcessIdentity identity)
        { this.ledger = ledger; AppId = appId; OwnerId = ownerId; Identity = identity; }
        private void Current(ResourceRecord record)
        {
            if (record.AppId != AppId || record.OwnerId != OwnerId || record.Owner != Identity || ledger.Driver.CurrentIdentity().Normalize() != Identity)
                throw new InvalidOperationException("Resource mutation requires the exact surviving owner generation.");
        }
        public ResourceRecord BeginLaunch(string label, string kind = "process", string ownership = "private", string cleanupMode = "report") => ledger.Locked(() =>
        {
            if (closing) throw new InvalidOperationException("Resource owner is closing; new admission is fenced.");
            var records = ledger.Records();
            if (records.Length >= 4096 || records.Count(r => r.AppId == AppId && !r.Terminal) >= ledger.MaximumActive)
                throw new InvalidOperationException("Resource admission capacity reached; recover prior resources without erasing their evidence.");
            var record = new ResourceRecord { ResourceId = Guid.NewGuid().ToString("N"), AppId = AppId, OwnerId = OwnerId, Owner = Identity,
                Label = label, Kind = kind, Ownership = ownership, CleanupMode = cleanupMode, CreatedAt = Now, UpdatedAt = Now }.Normalize();
            Current(record);
            if (File.Exists(ledger.RecordPath(record.ResourceId))) throw new InvalidOperationException("Resource identity already exists.");
            ledger.Save(record); return record;
        });
        public ResourceRecord BindProcess(string id, uint pid, string containment = "app_owned") => ledger.Locked(() =>
        {
            var record = ledger.Get(id); Current(record);
            if (record.State != "launching" || record.Process is not null) throw new InvalidOperationException("Resource already bound or not awaiting launch.");
            var candidate = (record with { Process = ledger.Driver.Capture(pid).Normalize(), State = "active", Containment = containment }).Normalize();
            if (candidate.Ownership == "private" && ledger.Conflict(candidate)) throw new InvalidOperationException("Another retained claim references this process.");
            return ledger.Replace(candidate);
        });
        // A stateless adapter is created suspended in its owner's kill-on-close
        // job first. Publish its full identity in one admission write, before
        // resume, so refusal/crash cannot leave a new unbound reservation.
        internal ResourceRecord AdmitSuspendedFrontend(string id, string label, ProcessIdentity suspended) => ledger.Locked(() =>
        {
            ResourceRules.Token(id, 32);
            if (closing || AppId != "rockwell-mcp-server" || ledger.Driver.CurrentIdentity().Normalize() != Identity)
                throw new InvalidOperationException("Stateless admission requires the exact current open Rockwell owner.");
            var process = ledger.Driver.Capture(suspended.Pid).Normalize();
            if (process != suspended.Normalize()) throw new InvalidOperationException("Suspended adapter identity changed before admission.");
            var records = ledger.Records();
            if (records.Length >= 4096 || records.Count(r => r.AppId == AppId && !r.Terminal) >= ledger.MaximumActive)
                throw new InvalidOperationException("Resource admission capacity reached; no stateless reservation was created.");
            if (File.Exists(ledger.RecordPath(id))) throw new InvalidOperationException("Resource identity already exists.");
            var record = new ResourceRecord { ResourceId = id, AppId = AppId, OwnerId = OwnerId, Owner = Identity,
                Label = label, Kind = "mcp-adapter", Ownership = "private", CleanupMode = "terminate", State = "active",
                Process = process, Containment = "windows_job", CreatedAt = Now, UpdatedAt = Now }.Normalize();
            if (ledger.Conflict(record)) throw new InvalidOperationException("Another retained claim references the suspended adapter.");
            ledger.Save(record); return record;
        });
        // A native SDK/COM connection may identify a shared software/session
        // without exposing an exclusive PID. It stays report-only and must be
        // explicitly detached; this never invents an executable to terminate.
        public ResourceRecord ActivateOpaqueAttachment(string id) => ledger.Locked(() =>
        {
            var record = ledger.Get(id); Current(record);
            if (record.State != "launching" || record.Ownership == "private" || record.Process is not null)
                throw new InvalidOperationException("Only an unbound attached/shared software claim can be activated without a PID.");
            return ledger.Replace(record with { State = "active" });
        });
        internal ResourceRecord FailBeforeExecution(string id) => ledger.Locked(() =>
        {
            var record = ledger.Get(id); Current(record);
            if (record.Receipt is not null || record.State is not ("launching" or "active") ||
                record.Process is not null && ledger.Driver.State(record.Process) is not ("absent" or "different"))
                throw new InvalidOperationException("An executed/live launch cannot be declared unstarted.");
            return ledger.Replace(record with { State = "failed" });
        });
        public ResourceRecord PublishMembers(string id, ProcessIdentity[] members) => ledger.Locked(() =>
        {
            var record = ledger.Get(id); Current(record);
            if (record.State != "active") throw new InvalidOperationException("Only an active resource can publish membership.");
            var candidate = (record with { Members = members }).Normalize();
            return candidate.Members.SequenceEqual(record.Members) ? record : ledger.Replace(candidate);
        });
        public ResourceRecord SetRecoveryBarrier(string id, string reason) => ledger.Locked(() =>
        {
            ResourceRules.Text(reason, 1, 512); var record = ledger.Get(id); Current(record);
            return ledger.Replace(record with { RecoveryBarrier = reason });
        });
        public ResourceRecord ResolveRecoveryBarrier(string id, string revision, string evidence) => ledger.Locked(() =>
        {
            var record = ledger.Get(id); Current(record);
            if (record.Revision != revision || record.RecoveryBarrier is null || record.Receipt is not null) throw new InvalidOperationException("A reviewed unresolved barrier without a cleanup receipt is required.");
            var attestation = new ResourceReconciliation(OwnerId, Identity, revision, evidence, Now).Normalize();
            return ledger.Replace(record with { RecoveryBarrier = null, BarrierResolution = attestation });
        });
        public ResourceRecord ReleaseAttachment(string id) => ledger.Locked(() =>
        {
            var record = ledger.Get(id); Current(record);
            if (record.Ownership == "private") throw new InvalidOperationException("A live private resource cannot be forgotten as an attachment.");
            return ledger.Replace(record with { State = "released" });
        });
        internal const string FrontdoorAppId = "rockwell-mcp-frontdoor";
        internal const string FrontdoorLabel = "Rockwell MCP metadata frontdoor";
        internal ResourceRecord AdmitFrontdoor() => ledger.Locked(() =>
        {
            if (closing || AppId != FrontdoorAppId || ledger.Driver.CurrentIdentity().Normalize() != Identity)
                throw new InvalidOperationException("Frontdoor admission requires its actual current process owner.");
            var records = ledger.Records();
            if (records.Length >= 4096 || records.Count(r => r.AppId == AppId && !r.Terminal) >= ledger.MaximumActive)
                throw new InvalidOperationException("Metadata frontdoor claim budget reached; retain unresolved evidence.");
            var record = new ResourceRecord { ResourceId = Guid.NewGuid().ToString("N"), AppId = AppId, OwnerId = OwnerId, Owner = Identity,
                Label = FrontdoorLabel, Kind = "mcp-frontdoor", Ownership = "attached", CleanupMode = "report", State = "active",
                CreatedAt = Now, UpdatedAt = Now }.Normalize();
            ledger.Save(record); return record;
        });
        private static bool FrontdoorShape(ResourceRecord record) =>
            record.AppId == FrontdoorAppId && record.Kind == "mcp-frontdoor" && record.Label == FrontdoorLabel &&
            record.State == "active" && record.Ownership == "attached" && record.CleanupMode == "report" &&
            record.Containment == "app_owned" && record.Process is null && record.Members.Length == 0 &&
            record.Receipt is null && record.Reconciliation is null && record.RecoveryBarrier is null && record.BarrierResolution is null;
        internal ResourceRecord CloseFrontdoor(string id, string revision) => ledger.Locked(() =>
        {
            var record = ledger.Get(id); Current(record);
            if (AppId != FrontdoorAppId || record.Revision != revision || !FrontdoorShape(record))
                throw new InvalidOperationException("Frontdoor detach requires its unchanged, exact current observation.");
            return ledger.Replace(record with { State = "released", Reconciliation = new ResourceReconciliation(OwnerId, Identity, revision,
                "Actual transport owner detached its stateless metadata observation after closing its admission handles. No process termination or engineering-job completion is asserted.", Now).Normalize() });
        });
        internal ResourceRecord? ReconcileExitedFrontdoor(string id, string revision, string reviewedImage) => ledger.Locked(() =>
        {
            if (closing || AppId != FrontdoorAppId || ledger.Driver.CurrentIdentity().Normalize() != Identity)
                throw new InvalidOperationException("Frontdoor reconciliation requires its actual current owner.");
            var record = ledger.Get(id);
            if (record.Revision != revision || !FrontdoorShape(record) ||
                !string.Equals(record.Owner.Executable, Path.GetFullPath(reviewedImage), StringComparison.OrdinalIgnoreCase) ||
                ledger.Driver.State(record.Owner) is not ("absent" or "different") ||
                ledger.Records().Any(other => other.ResourceId != id && !other.Terminal &&
                    (other.Owner == record.Owner || other.Process == record.Owner || other.Members.Contains(record.Owner)))) return null;
            return ledger.Replace(record with { State = "released", Reconciliation = new ResourceReconciliation(OwnerId, Identity, revision,
                "Exact departed stateless frontdoor process generation independently verified. Observation only; no process action, native recovery or operation replay.", Now).Normalize() });
        });
        public ResourceRecord Reconcile(string id, string revision, string evidence) => ledger.Locked(() =>
        {
            var record = ledger.Get(id);
            if (record.AppId != AppId || record.Revision != revision || ledger.Driver.CurrentIdentity().Normalize() != Identity || record.Terminal || record.Reconciliation is not null)
                throw new InvalidOperationException("Reconciliation requires the same app, reviewed unresolved revision and current actor.");
            var current = record.OwnerId == OwnerId && record.Owner == Identity;
            if (!current && ledger.Driver.State(record.Owner) is not ("absent" or "different")) throw new InvalidOperationException("Another owner is live or cannot be verified.");
            if (record.Ownership == "private" && (record.Process is not null && ledger.Driver.State(record.Process) is not ("absent" or "different") || record.Members.Any(m => ledger.Driver.State(m) is not ("absent" or "different"))))
                throw new InvalidOperationException("Inspect/close the private resource before attesting recovery.");
            var attestation = new ResourceReconciliation(OwnerId, Identity, revision, evidence, Now).Normalize();
            return ledger.Replace(record with { State = "released", Reconciliation = attestation });
        });
        // Stateless MCP transports may outlive their supervisor's managed
        // finally block. A later real owner can retire only fully identified,
        // independently exited generations. Unbound launch reservations and
        // any native/shared/uncertain claim remain operator recovery work.
        internal bool CanReconcileClosedMcpFrontend(ResourceRecord record, string adapterExecutable, string? reviewedPreviousOwner = null)
        {
            if (closing || ledger.Driver.CurrentIdentity().Normalize() != Identity)
                throw new InvalidOperationException("Frontend reconciliation requires the exact current open owner.");
            if (AppId != "rockwell-mcp-server" || record.AppId != AppId ||
                record.Kind != "mcp-adapter" || record.State != "active" || record.Ownership != "private" ||
                record.CleanupMode != "terminate" || record.Containment != "windows_job" ||
                record.Process is null || record.Receipt is not null || record.RecoveryBarrier is not null ||
                record.Reconciliation is not null || record.BarrierResolution is not null ||
                !string.Equals(record.Owner.Executable, reviewedPreviousOwner ?? Identity.Executable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.Process.Executable, Path.GetFullPath(adapterExecutable), StringComparison.OrdinalIgnoreCase)) return false;
            var console = Path.Combine(Environment.SystemDirectory, "conhost.exe");
            if (record.Members.Any(m => !string.Equals(m.Executable, adapterExecutable, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(m.Executable, console, StringComparison.OrdinalIgnoreCase))) return false;
            if (ledger.Driver.State(record.Owner) is not ("absent" or "different") ||
                ledger.Driver.State(record.Process) is not ("absent" or "different") ||
                record.Members.Any(m => ledger.Driver.State(m) is not ("absent" or "different"))) return false;
            var identities = record.Members.Append(record.Process).ToHashSet();
            // Recovery never borrows the nested-service cleanup exemption.
            return !ledger.Records().Any(other => other.ResourceId != record.ResourceId && !other.Terminal &&
                (other.Process is not null && identities.Contains(other.Process) || other.Members.Any(identities.Contains)));
        }
        internal ResourceRecord? ReconcileClosedMcpFrontend(string id, string revision, string adapterExecutable, string? reviewedPreviousOwner = null) => ledger.Locked(() =>
        {
            var record = ledger.Get(id);
            if (record.Revision != revision || !CanReconcileClosedMcpFrontend(record, adapterExecutable, reviewedPreviousOwner)) return null;
            var attestation = new ResourceReconciliation(OwnerId, Identity, revision,
                "Exact prior MCP owner, adapter and recorded members are independently gone. Stateless transport only; no process action or native recovery performed.", Now).Normalize();
            return ledger.Replace(record with { State = "released", Reconciliation = attestation });
        });
        internal bool CanReconcileClosedOfflineWorker(ResourceRecord record, string serviceExecutable, string workerExecutable)
        {
            if (closing || ledger.Driver.CurrentIdentity().Normalize() != Identity)
                throw new InvalidOperationException("Offline worker recovery requires the exact current open owner.");
            if (AppId != "rockwell-mcp-server" || record.AppId != AppId || record.Kind != "extension-worker" ||
                record.State != "active" || record.Ownership != "private" || record.CleanupMode != "terminate" || record.Containment != "windows_job" ||
                record.Process is null || record.Receipt is not null || record.RecoveryBarrier is not null || record.Reconciliation is not null || record.BarrierResolution is not null ||
                !string.Equals(record.Owner.Executable, serviceExecutable, StringComparison.OrdinalIgnoreCase) || !string.Equals(record.Process.Executable, workerExecutable, StringComparison.OrdinalIgnoreCase)) return false;
            var console = Path.Combine(Environment.SystemDirectory, "conhost.exe");
            if (record.Members.Any(m => !string.Equals(m.Executable, workerExecutable, StringComparison.OrdinalIgnoreCase) && !string.Equals(m.Executable, console, StringComparison.OrdinalIgnoreCase))) return false;
            if (new[] { record.Owner, record.Process }.Concat(record.Members).Any(p => ledger.Driver.State(p) is not ("absent" or "different"))) return false;
            var identities = record.Members.Append(record.Process).ToHashSet();
            return !ledger.Records().Any(other => other.ResourceId != record.ResourceId && !other.Terminal &&
                (other.Process is not null && identities.Contains(other.Process) || other.Members.Any(identities.Contains)));
        }
        internal ResourceRecord? ReconcileClosedOfflineWorker(string id, string revision, string serviceExecutable, string workerExecutable) => ledger.Locked(() =>
        {
            var record = ledger.Get(id);
            if (record.Revision != revision || !CanReconcileClosedOfflineWorker(record, serviceExecutable, workerExecutable)) return null;
            return ledger.Replace(record with { State = "released", Reconciliation = new ResourceReconciliation(OwnerId, Identity, revision,
                "Pinned offline service owner, extension worker and recorded members are independently gone. Process metadata only; operation/native journals remain retained and no job is replayed.", Now).Normalize() });
        });
        internal bool CanRetireUnboundStatelessFrontend(ResourceRecord record, string reviewedHost)
        {
            if (closing || AppId != "rockwell-mcp-server" || record.AppId != AppId || ledger.Driver.CurrentIdentity().Normalize() != Identity ||
                record.Kind != "mcp-adapter" || record.Label != "Rockwell MCP stdio adapter" || record.State != "launching" || record.Process is not null ||
                record.Members.Length != 0 || record.Ownership != "private" || record.CleanupMode != "terminate" || record.Containment != "app_owned" ||
                record.Receipt is not null || record.RecoveryBarrier is not null || record.Reconciliation is not null || record.BarrierResolution is not null ||
                !string.Equals(record.Owner.Executable, reviewedHost, StringComparison.OrdinalIgnoreCase) ||
                ledger.Driver.State(record.Owner) is not ("absent" or "different")) return false;
            return !ledger.Records().Any(other => other.ResourceId != record.ResourceId && !other.Terminal &&
                (other.Owner == record.Owner || other.Process == record.Owner || other.Members.Contains(record.Owner)));
        }
        internal ResourceRecord? RetireUnboundStatelessFrontend(string id, string revision, string reviewedHost, string proofHash) => ledger.Locked(() =>
        {
            ResourceRules.Token(proofHash, 64); var record = ledger.Get(id);
            if (record.Revision != revision || !CanRetireUnboundStatelessFrontend(record, reviewedHost)) return null;
            return ledger.Replace(record with { State = "released", Reconciliation = new ResourceReconciliation(OwnerId, Identity, revision,
                "Reviewed legacy stateless launch proof " + proofHash + ": original full owner is gone; unpublished children were suspended in its non-inherited kill-on-close job. No child PID invented; native/customer journals remain retained.", Now).Normalize() });
        });
        public CleanupReceipt Cleanup(string id, string revision, string requestId, IResourceProcessGroup? group = null) => ledger.Locked(() =>
        {
            ResourceRules.Token(requestId, 32); var record = ledger.Get(id); Current(record);
            if (record.Receipt is not null)
            {
                if (record.Receipt.RequestId == requestId && record.Receipt.ExpectedRevision == revision) return record.Receipt;
                throw new InvalidOperationException("Cleanup already claimed; inspect its retained receipt instead of replaying.");
            }
            if (record.Revision != revision || ledger.Records().Any(r => r.Receipt?.RequestId == requestId)) throw new InvalidOperationException("Cleanup revision/request identity conflict.");
            if (record.Ownership != "private" || record.CleanupMode != "terminate" || record.State != "active" || record.RecoveryBarrier is not null || record.Process is null || ledger.Conflict(record))
                throw new InvalidOperationException("Resource policy, recovery barrier or another retained claim blocks cleanup.");
            if (group is not null)
            {
                if (record.Containment != "windows_job" || group.Identity.Normalize() != record.Process) throw new InvalidOperationException("Process group is not this exact owned resource.");
                var candidate = (record with { Members = group.Identities() }).Normalize();
                if (ledger.Conflict(candidate)) throw new InvalidOperationException("Another retained claim references a process-tree member.");
                if (!candidate.Members.SequenceEqual(record.Members)) record = ledger.Replace(candidate);
            }
            var identity = record.Process ?? throw new InvalidOperationException("Resource process identity was lost.");
            var state = ledger.Driver.State(identity);
            if (state is not ("alive" or "absent" or "different")) throw new InvalidOperationException("Process identity is unavailable.");
            using var handle = state == "alive" ? ledger.Driver.Open(identity) : null;
            var receipt = new CleanupReceipt(requestId, revision, "dispatching", "Cleanup intent recorded; outcome not yet verified");
            var pending = ledger.Replace(record with { State = "uncertain", Receipt = receipt });
            try
            {
                if (group is not null) group.Stop();
                else if (handle is not null) { handle.Terminate(); if (!handle.Wait(5000)) throw new TimeoutException("Resource did not exit before cleanup deadline."); }
                receipt = receipt with { Outcome = group is not null || handle is not null ? "terminated" : "already_exited", Message = group is not null ? "Owned process tree stopped" : "Exact recorded process is gone" };
                ledger.Replace(pending with { State = "terminated", Receipt = receipt });
            }
            catch (Exception)
            {
                receipt = receipt with { Outcome = "uncertain", Message = "Inspect actual app/resource state; cleanup must not be replayed" };
                ledger.Replace(pending with { Receipt = receipt });
            }
            return receipt;
        });
        public ResourceRecord[] FenceAndInspectClose() => ledger.Locked(() =>
        {
            if (ledger.Driver.CurrentIdentity().Normalize() != Identity) throw new InvalidOperationException("Only the exact current owner may fence resource admission.");
            closing = true;
            return ledger.Records().Where(r => r.AppId == AppId && r.OwnerId == OwnerId && r.Owner == Identity && !r.Terminal).ToArray();
        });
    }
}
