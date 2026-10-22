namespace Studio5000.Core.Resources;

// Byte transport only: the existing MCP SDK remains the JSON-RPC parser and
// owns its stdin/stdout semantics. This owner never starts the HTTP service.
public static class OwnedStdioRelay
{
    public static async Task<int> RunAsync(WindowsChildProcess child, Stream input, Stream output, Stream error,
        int shutdownSeconds, CancellationToken ownerLost)
    {
        if (shutdownSeconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(shutdownSeconds));
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(ownerLost);
        async Task Feed()
        {
            try { await input.CopyToAsync(child.StandardInput.BaseStream, 65536, pumps.Token); }
            finally { child.StandardInput.Close(); } // Real EOF reaches the SDK.
        }
        var feed = Feed();
        var drain = child.StandardOutput.BaseStream.CopyToAsync(output, 65536, pumps.Token);
        var diagnostics = child.StandardError.BaseStream.CopyToAsync(error, 65536, pumps.Token);
        var exit = child.WaitForExitAsync();
        var lost = Task.Delay(Timeout.Infinite, ownerLost);
        try
        {
            var first = await Task.WhenAny(feed, drain, exit, lost);
            if (first == feed || first == drain)
            {
                await first;
                // EOF gets a bounded cooperative exit before private-job close.
                try { await exit.WaitAsync(TimeSpan.FromSeconds(shutdownSeconds), ownerLost); }
                catch (TimeoutException) { child.Kill(); }
            }
            else if (first != exit && !child.HasExited) child.Kill();
            await exit;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(shutdownSeconds));
            await Task.WhenAll(drain, diagnostics).WaitAsync(deadline.Token);
            await output.FlushAsync(deadline.Token); await error.FlushAsync(deadline.Token);
            return child.ExitCode;
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            pumps.Cancel();
            // A synchronous console stdin may ignore cancellation; do not wait
            // forever for it after the private child and its job are closed.
            foreach (var pump in new[] { feed, drain, diagnostics })
                _ = pump.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
