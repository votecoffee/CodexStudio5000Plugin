using Studio5000.Core.Resources;

namespace Studio5000.Core;

// Inspect readiness again after owner loss. The engineering HTTP request has
// not been sent yet; Ensure itself fences every unresolved startup dispatch.
public sealed class OfflineServiceGate
{
    private readonly object sync = new();
    private readonly Func<Task<OfflineServiceReady>> ensure;
    private readonly IResourceProcessDriver driver;
    private readonly bool existingOnly;
    private Task<OfflineServiceReady>? current;
    public OfflineServiceGate(string root, string backend, ResourceLedger.ResourceOwner actor, CancellationToken lifetime = default)
        : this(() => OfflineServiceBootstrap.EnsureAsync(root, backend, actor, lifetime), actor.Ledger.Driver) { }
    internal OfflineServiceGate(Func<Task<OfflineServiceReady>> ensure, IResourceProcessDriver driver, bool existingOnly = false)
    { this.ensure = ensure; this.driver = driver; this.existingOnly = existingOnly; }
    public static OfflineServiceGate Observer(string root, string backend, ResourceLedger.ResourceOwner actor)
        => new(() => OfflineServiceBootstrap.ObserveAsync(root, backend, actor), actor.Ledger.Driver);
    public static OfflineServiceGate ExistingOnly(string root, string backend, ResourceLedger.ResourceOwner actor, CancellationToken lifetime = default)
        => new(() => OfflineServiceBootstrap.ObserveExistingAsync(root, backend, actor, lifetime), actor.Ledger.Driver, existingOnly: true);
    public async Task<OfflineServiceReady> Ready(CancellationToken ct = default)
    {
        if (existingOnly)
        {
            // Resolve one already-running genuine generation once per connection.
            // A failed first observation or departed owner stays refused until a
            // new frontend connection; never reenter ensure or adopt a replacement.
            try
            {
                Task<OfflineServiceReady> observation;
                lock (sync) observation = current ??= Task.Run(ensure);
                var existing = await observation.WaitAsync(ct);
                if (driver.State(existing.Owner) != "alive") throw new ExistingServiceUnavailableException();
                return existing;
            }
            catch (Exception error) when (error is not (OperationCanceledException or ExistingServiceUnavailableException))
            { throw new ExistingServiceUnavailableException(error); }
        }
        for (var generation = 0; generation < 2; generation++)
        {
            Task<OfflineServiceReady> task;
            lock (sync)
            {
                if (current is null || current.IsFaulted || current.IsCanceled ||
                    current.IsCompletedSuccessfully && driver.State(current.Result.Owner) is "absent" or "different")
                    current = Task.Run(ensure);
                task = current;
            }
            var result = await task.WaitAsync(ct);
            var state = driver.State(result.Owner);
            if (state == "alive") return result;
            if (state is not ("absent" or "different")) throw new InvalidOperationException("Current service owner is unverifiable; no engineering request was dispatched.");
        }
        throw new InvalidOperationException("Service owner changed repeatedly during readiness; inspect before retrying the request.");
    }
    public async Task Maintain(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested(); await Ready(ct); await Task.Delay(1000, ct);
        }
    }
}

public sealed class ExistingServiceUnavailableException(Exception? inner = null)
    : Exception("The existing genuine service is unavailable or unverified. This connection cannot start, replace or reattach a service; no engineering request was dispatched. Reconnect only after the service is available.", inner)
{
    public const string Code = "mcp_existing_service_unavailable";
}
