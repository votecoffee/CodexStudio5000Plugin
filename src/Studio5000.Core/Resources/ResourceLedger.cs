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
            file.Lock(0, 1);
            try { return action(); } finally { file.Unlock(0, 1); }
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
