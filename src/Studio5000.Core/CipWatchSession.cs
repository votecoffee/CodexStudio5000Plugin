using System.Diagnostics;
using System.Text.Json;
using Studio5000.Core.Resources;

namespace Studio5000.Core;

internal sealed class WatchBackendRouter(IWatchSessionFactory sdk, IWatchSessionFactory cip) : IWatchSessionFactory
{
    public Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct) => request.Backend switch
    {
        "Sdk" => sdk.OpenAsync(request, ct), "LibPlcTag" => cip.OpenAsync(request, ct),
        _ => throw new OperationException("watch_backend", "Unknown watch backend.")
    };
}

/// <summary>An optional isolated read-only worker; starting it is owned by the shared watch policy/scheduler.</summary>
internal sealed class CipWatchSessionFactory(ServiceOptions options) : IWatchSessionFactory
{
    public async Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct)
    {
        if (!File.Exists(options.CipBridgePath)) throw new OperationException("cip_unavailable", "Build and configure the optional CipBridge worker.");
        if (request.Backend != "LibPlcTag" || request.Mode != "Online" || request.CipTarget is null || request.AllowedTags is null)
            throw new OperationException("cip_request", "An online CIP session requires a target and an exact selection list.");
        CipWatchPlanning.ValidateTarget(request.CipTarget);
        var plan = CipWatchPlanning.Build(XmlProjects.Parse(await File.ReadAllBytesAsync(request.SourcePath, ct)), "", request.AllowedTags);
        var start = new ProcessStartInfo(Path.GetFullPath(options.CipBridgePath!))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var process = ResourceSupervisor.RequireCurrent().Launch(start, "Rockwell read-only CIP session", "cip-reader");
        var session = new Session(process, TimeSpan.FromMilliseconds((long)request.CipTarget.TimeoutMs * Math.Max(1, plan.UniqueLeafReads) * 2 + 5000));
        try { await session.ExchangeAsync(request, ct); return session; }
        catch { await session.DisposeAsync(); throw; }
    }

    private sealed class Session(WindowsChildProcess process, TimeSpan readTimeout) : IWatchSession
    {
        private static readonly JsonSerializerOptions WireJson = new(ProjectService.Json) { WriteIndented = false };
        private readonly Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        public async Task<WatchBridgeReply> ExchangeAsync<T>(T request, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(readTimeout);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, WireJson).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("CIP reader exited; inspect and restart explicitly.");
            var reply = JsonSerializer.Deserialize<WatchBridgeReply>(line, WireJson) ?? throw new IOException("CIP reader returned an empty reply.");
            if (!reply.Success) throw new OperationException("cip_reader", reply.Error ?? "CIP reader failed.");
            return reply;
        }
        public Task<WatchBridgeReply> ReadAsync(WatchTag[] tags, CancellationToken ct) => ExchangeAsync(new WatchBridgeCommand(tags), ct);
        public async ValueTask DisposeAsync()
        {
            try
            {
                process.StandardInput.Close(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            finally { await stderr; process.Dispose(); }
        }
    }
}
