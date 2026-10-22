using System.Diagnostics;
using System.Text.Json;
using Studio5000.Core.Resources;

namespace Studio5000.Core;

public sealed partial class NativeConverter : IWatchSessionFactory
{
    public async Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct)
    {
        if (!Available) throw new OperationException("native_unavailable", "Build and configure the native bridge.");
        ResourceSupervisor.RequireCurrent().CheckWorkerAdmission();
        await sdkService.EnsureForSdkAsync(ct);
        if (Path.GetExtension(request.SourcePath).Equals(".l5x", StringComparison.OrdinalIgnoreCase))
        {
            // Import XML once. Opening two L5X copies repeats a costly native import; readers use ACD copies thereafter.
            var acd = Path.Combine(request.WorkDirectory, "source-native.acd");
            await ConvertAsync(request.SourcePath, acd, ct);
            request = request with { SourcePath = acd };
        }
        var start = Start(); start.ArgumentList.Add("--watch");
        var worker = ResourceSupervisor.RequireCurrent().LaunchNative(start, "Rockwell SDK watch session");
        var session = new NativeWatchSession(worker, gate, options.NativeTimeoutSeconds);
        try { await session.ExchangeAsync<WatchBridgeOpen, WatchBridgeReply>(request, ct); return session; }
        catch { await session.DisposeAsync(); throw; }
    }

    private sealed class NativeWatchSession : IWatchSession
    {
        private readonly OwnedNativeWorker worker;
        private readonly WindowsChildProcess process;
        private readonly SemaphoreSlim nativeGate;
        private readonly int timeoutSeconds;
        private readonly Task stderr;
        public NativeWatchSession(OwnedNativeWorker worker, SemaphoreSlim nativeGate, int timeoutSeconds)
        { this.worker = worker; process = worker.Process; this.nativeGate = nativeGate; this.timeoutSeconds = timeoutSeconds; stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null); }
        private static readonly JsonSerializerOptions WireJson = new(ProjectService.Json) { WriteIndented = false };
        public async Task<TResponse> ExchangeAsync<TRequest, TResponse>(TRequest request, CancellationToken ct)
        {
            await nativeGate.WaitAsync(ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, WireJson).AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Watch reader exited; restart the list explicitly after inspection.");
                var value = JsonSerializer.Deserialize<TResponse>(line, WireJson) ?? throw new IOException("Watch reader returned an empty reply.");
                if (value is WatchBridgeReply { Success: false } failed) throw new OperationException("watch_reader", failed.Error ?? "Watch reader failed.");
                return value;
            }
            finally { nativeGate.Release(); }
        }
        public Task<WatchBridgeReply> ReadAsync(WatchTag[] tags, CancellationToken ct) => ExchangeAsync<WatchBridgeCommand, WatchBridgeReply>(new(tags), ct);
        public async ValueTask DisposeAsync()
        {
            try
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            finally { await stderr; worker.Dispose(); }
            if (worker.CloseResult?.Outcome != "closed") throw new OperationException("native_recovery_required", "SDK watch stopped without matching native close evidence; inspect the retained session.");
        }
    }
}
