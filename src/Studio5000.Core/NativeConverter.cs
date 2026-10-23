using System.Diagnostics;
using System.Text.Json;
using Studio5000.Core.Resources;

namespace Studio5000.Core;

public sealed partial class NativeConverter(ServiceOptions options, SdkServiceControl sdkService)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Available => !string.IsNullOrWhiteSpace(options.NativeBridgePath) && File.Exists(options.NativeBridgePath);

    public async Task<JsonElement> CoverageAsync(CancellationToken ct)
    {
        if (!Available) throw new OperationException("native_unavailable", "Build and configure the installed SDK native bridge.");
        var start = Start(); start.ArgumentList.Add("--sdk-coverage");
        using var process = ResourceSupervisor.RequireCurrent().Launch(start, "Rockwell SDK coverage inspection", "sdk-inspection");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new OperationException("native_failed", "SDK coverage process failed: " + await error);
        using var doc = JsonDocument.Parse(await output); return doc.RootElement.Clone();
    }

    private ProcessStartInfo Start()
    {
        NativeExecutionPolicy.ValidateOptions(options);
        var start = new ProcessStartInfo(Path.GetFullPath(options.NativeBridgePath!))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment[NativeExecutionPolicy.EnvironmentKey] = options.NativeExecutionProfile;
        return start;
    }

    public async Task<SdkBridgeResult> ExecuteSdkAsync(SdkBridgeRequest request, CancellationToken ct)
    {
        NativeExecutionPolicy.ValidateOptions(options);
        NativeExecutionPolicy.ValidateCalls(options.NativeExecutionProfile, request.Calls, request.CommunicationsPath);
        request = request with { ExecutionProfile = options.NativeExecutionProfile };
        if (!Available) throw new OperationException("native_unavailable", "Build and configure the installed SDK native bridge.");
        await gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            ResourceSupervisor.RequireCurrent().CheckWorkerAdmission();
            await sdkService.EnsureForSdkAsync(ct);
            var resultPath = Path.Combine(request.WorkDirectory, "result.json");
            var start = Start(); start.ArgumentList.Add("--sdk"); start.ArgumentList.Add(resultPath);
            using var worker = ResourceSupervisor.RequireCurrent().LaunchNative(start, "Rockwell SDK operation session");
            var process = worker.Process;
            // Password arguments travel only over stdin. No raw request, stdout or stderr is persisted.
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.NativeTimeoutSeconds));
            try
            {
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, ProjectService.Json).AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(output, error);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                return new("indeterminate", [], [], "SDK process was cancelled or timed out. Completed controller effects cannot be inferred. Inspect the controller before retrying.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                return new("indeterminate", [], [], "SDK process communication failed. Completed controller effects cannot be inferred.");
            }
            if (process.ExitCode != 0 || !File.Exists(resultPath))
                return new("indeterminate", [], [], "SDK process exited without a complete result. Inspect any external target before retrying.");
            var result = JsonSerializer.Deserialize<SdkBridgeResult>(await File.ReadAllTextAsync(resultPath, CancellationToken.None), ProjectService.Json)!;
            worker.Dispose();
            return worker.CloseResult?.Outcome == "closed" ? result : result with { Status = "indeterminate", Error = "Native save/close/effect evidence is incomplete. Inspect the retained session and original operation result before retrying." };
        }
        finally { gate.Release(); }
    }

    public async Task ConvertAsync(string input, string output, CancellationToken ct)
    {
        if (!Available)
            throw new OperationException("native_unavailable", "Build the native bridge and configure NativeBridgePath to enable ACD operations.");
        await gate.WaitAsync(ct);
        try
        {
            ResourceSupervisor.RequireCurrent().CheckWorkerAdmission();
            await sdkService.EnsureForSdkAsync(ct);
            var start = Start();
            start.ArgumentList.Add(input);
            start.ArgumentList.Add(output);
            using var worker = ResourceSupervisor.RequireCurrent().LaunchNative(start, "Rockwell native project conversion");
            var process = worker.Process; process.StandardInput.Close();
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
            worker.Dispose();
            if (worker.CloseResult?.Outcome != "closed") throw new OperationException("native_recovery_required", "Conversion returned without matching document/connection close evidence; inspect the retained native session.");
        }
        finally { gate.Release(); }
    }
}
