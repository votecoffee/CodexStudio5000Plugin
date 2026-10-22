namespace Studio5000.Core.Resources;

// The process is a private owned worker. Its native connection/document lease is
// separate and report-only: interrupting the worker never clears native effects.
public sealed class OwnedNativeWorker(WindowsChildProcess process, NativeSessionLease session, string receiptPath, string sessionId) : IDisposable
{
    public WindowsChildProcess Process { get; } = process;
    public NativeSessionLease Session { get; } = session;
    private bool disposed;
    public NativeCloseResult? CloseResult { get; private set; }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        var graceful = Process.HasExited && Process.ExitCode == 0;
        // Keep the native lease/barrier on every failure, including a lost
        // private-process cleanup result. There is no native close retry here.
        try
        {
            var cleanup = Process.Close();
            CloseResult = Session.Close(() => NativeLifecycleJournal.VerifyClose(receiptPath, sessionId, Process.Identity, graceful && cleanup.Outcome != "uncertain"));
        }
        catch
        {
            if (Session.State.State is "open" or "refused") CloseResult = Session.Close(() => NativeCloseResult.Uncertain("Native worker cleanup or receipt readback failed; inspect retained records."));
            throw;
        }
    }
}
