using System.Diagnostics;

namespace Studio5000.Core;

public sealed class NativeConverter(ServiceOptions options)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Available => !string.IsNullOrWhiteSpace(options.NativeBridgePath) && File.Exists(options.NativeBridgePath);

    public async Task ConvertAsync(string input, string output, CancellationToken ct)
    {
        if (!Available)
            throw new OperationException("native_unavailable", "Build the native bridge and configure NativeBridgePath to enable ACD operations.");
        await gate.WaitAsync(ct);
        try
        {
            var start = new ProcessStartInfo(options.NativeBridgePath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(input);
            start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new IOException("Could not start native bridge.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.NativeTimeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw new OperationException("native_cancelled", "Native conversion timed out or was cancelled; no result was published.");
            }
            var outputLog = await stdout;
            var errorLog = await stderr;
            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                throw new OperationException("native_failed", $"Logix SDK conversion failed ({process.ExitCode}). {errorLog} {outputLog}"[..Math.Min(4000, $"Logix SDK conversion failed ({process.ExitCode}). {errorLog} {outputLog}".Length)]);
        }
        finally { gate.Release(); }
    }
}
