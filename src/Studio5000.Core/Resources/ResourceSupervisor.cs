using System.Diagnostics;
using System.Text.Json;

namespace Studio5000.Core.Resources;

// One actual surviving service or stdio-supervisor process owns this scope.
// It never adopts a prior generation or starts a registration helper.
public sealed class ResourceSupervisor : IDisposable
{
    private static ResourceSupervisor? current;
    private readonly object sync = new();
    private readonly List<WindowsChildProcess> children = [];
    private bool closing, disposed;
    private bool serviceReceiptPublished;
    public string Root { get; }
    public ResourceLedger Ledger { get; }
    public ResourceLedger.ResourceOwner Owner { get; }
    public NativeSessionRegistry NativeSessions { get; }
    private readonly NativeGuiHandoff nativeGui;
    public int MaximumProcesses { get; }
    public static bool IsInitialized => Volatile.Read(ref current) is not null;
    public static ResourceSupervisor RequireCurrent() => current ?? throw new OperationException("resource_owner_required", "Initialize the long-lived application resource owner before launching workers; no untracked fallback is allowed.");
    public ResourceSupervisor(string root, string registry, int maximumProcesses = 32, int maximumResources = 32)
    {
        Root = Path.GetFullPath(root); MaximumProcesses = maximumProcesses; OwnedProcessContainment.ValidateLimit(maximumProcesses);
        if (File.Exists(Path.Combine(Root, ".local", "maintenance-hold.json"))) throw new InvalidOperationException("Operator maintenance hold blocks application resource startup.");
        Ledger = new(registry, new WindowsResourceProcessDriver(), maximumResources, nestedServiceOwner: true); Owner = Ledger.Owner("rockwell-mcp-server");
        NativeSessions = new(Owner, Path.Combine(Root, ".local", "native-sessions"));
        nativeGui = new(NativeSessions, Path.Combine(Root, ".local", "native-gui-handoffs"), () => new WindowsUserShellHandoff());
        if (Interlocked.CompareExchange(ref current, this, null) is not null) throw new InvalidOperationException("This process already has a resource supervisor.");
    }
    public NativeGuiHandoffResult LaunchUserGui(string executable)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (closing || File.Exists(Path.Combine(Root, ".local", "maintenance-hold.json"))) throw new OperationException("resource_owner_closing", "GUI admission is fenced or under operator maintenance hold.");
            return nativeGui.Launch(executable);
        }
    }
    public OwnedNativeWorker LaunchNative(ProcessStartInfo start, string label)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (closing || File.Exists(Path.Combine(Root, ".local", "maintenance-hold.json"))) throw new OperationException("resource_owner_closing", "Native admission is fenced or under operator maintenance hold.");
            var id = Guid.NewGuid().ToString("N");
            var receipt = Path.Combine(Root, ".local", "native-sessions", id + ".worker.json");
            var lease = NativeSessions.Attach(new("rockwell-logix-sdk", id, "sdk-session"));
            start.Environment["ROCKWELL_NATIVE_LIFECYCLE"] = receipt;
            start.Environment["ROCKWELL_NATIVE_SESSION"] = id;
            try { return new(Launch(start, label, "sdk-worker"), lease, receipt, id); }
            catch
            {
                lease.Close(() => NativeCloseResult.Uncertain("Native worker admission failed; inspect the launch claim before attesting that no native code ran."));
                throw;
            }
        }
    }
    public WindowsChildProcess Launch(ProcessStartInfo start, string label, string kind)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (closing || File.Exists(Path.Combine(Root, ".local", "maintenance-hold.json"))) throw new OperationException("resource_owner_closing", "Worker admission is fenced or under operator maintenance hold.");
            children.RemoveAll(c => c.HandlesClosed);
            var child = WindowsChildProcess.Launch(Owner, start, label, kind, MaximumProcesses);
            children.Add(child); return child;
        }
    }
    public void WriteOwnerReceipt()
    {
        var local = Path.Combine(Root, ".local"); ProjectService.RejectLinks(local); Directory.CreateDirectory(local);
        var path = Path.Combine(local, "service-owner.json"); ProjectService.RejectLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, ownerId = Owner.OwnerId, owner = Owner.Identity, registry = Path.GetDirectoryName(Ledger.DirectoryPath) });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        serviceReceiptPublished = true;
    }
    public void Refresh()
    {
        lock (sync)
        {
            children.RemoveAll(c => c.HandlesClosed);
            foreach (var child in children) if (child.Record.State == "active") child.Refresh();
        }
    }
    public ResourceRecord[] ShutdownBlockers()
    {
        lock (sync)
            return Ledger.Records().Where(r => r.AppId == Owner.AppId && r.OwnerId == Owner.OwnerId && !r.Terminal &&
                (r.RecoveryBarrier is not null || r.Receipt is not null || r.Ownership != "private" || r.CleanupMode != "terminate" || r.State != "active")).ToArray();
    }
    public void CloseOwnedResources()
    {
        lock (sync)
        {
            if (disposed) return;
            if (ShutdownBlockers().Length != 0) throw new OperationException("native_recovery_required", "Owned documents, attachments, shared leases or uncertain receipts require app-owned close/recovery before shutdown.");
            closing = true; Owner.FenceAndInspectClose();
            foreach (var child in children.Where(c => !c.HandlesClosed))
                if (child.Close().Outcome == "uncertain") throw new OperationException("resource_cleanup_uncertain", "Resource cleanup is uncertain; keep admission fenced and inspect the retained receipt.");
            if (serviceReceiptPublished)
                NativeLifecycleJournal.WriteAtomic(Path.Combine(Root, ".local", "service-owner-exit.json"),
                    JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, ownerId = Owner.OwnerId, owner = Owner.Identity, state = "closed" }));
            children.Clear(); disposed = true; Interlocked.CompareExchange(ref current, null, this);
        }
    }
    public void Dispose() => CloseOwnedResources();
}
