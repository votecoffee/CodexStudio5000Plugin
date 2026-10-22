namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private int explicitSdkServiceRequests;
    public SdkServiceStatus SdkServiceStatus() => sdkService.Status();
    public async Task<SdkServiceReceipt> EnsureSdkServiceRunningAsync(SdkServiceEnsureRequest request, CancellationToken ct = default)
    {
        // Recovery must remain available while a project SDK job is blocked. It
        // has its own serialized SCM lane and shares the owner's shutdown fence.
        lock (watchLock)
        {
            if (shutdownRequested) throw new OperationException("service_stopping", "The shared service is preparing to stop.");
            explicitSdkServiceRequests++;
        }
        try { return await sdkService.EnsureRunningAsync(request, "explicit", ct); }
        finally { lock (watchLock) explicitSdkServiceRequests--; }
    }
}
