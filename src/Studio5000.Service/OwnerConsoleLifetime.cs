using Studio5000.Core.Resources;

// Console stop is an owner request. Default ConsoleLifetime would bypass native
// refusal by stopping the host before app-owned save/close review.
internal sealed class OwnerConsoleLifetime(OwnerShutdownMailbox mailbox) : IHostLifetime, IDisposable
{
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    { Console.CancelKeyPress += Cancel; return Task.CompletedTask; }
    private void Cancel(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        try { mailbox.RequestOnce(); }
        catch (Exception) { Console.Error.WriteLine("Owner stop request is unavailable; inspect retained shutdown/native records. Service ownership remains active."); }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() => Console.CancelKeyPress -= Cancel;
}
