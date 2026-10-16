using System.Text.Json;
using Studio5000.Core;

internal static class SdkOfflinePath
{
    public static async Task RunAsync(string root, string source)
    {
        var state = Path.Combine(root, "artifacts", "test-results", "sdk-offline-path-" + Guid.NewGuid().ToString("N"));
        const string path = @"AB_ETH-1\192.0.2.1\Backplane\0"; // TEST-NET address; configuration only. No connection API is called.
        using var service = new ProjectService(new ServiceOptions { StateDirectory = state, AllowedRoots = [Path.GetDirectoryName(source)!],
            NativeBridgePath = Path.Combine(root, "artifacts", "app", "NativeBridge", "Studio5000.NativeBridge.exe"),
            ControllerTargets = [new("offline-fixture", path)], EnableControllerOperations = false });
        var hash = ProjectService.Hash(await File.ReadAllBytesAsync(source));
        var job = await service.StartSdkJobAsync(new([
            new("SetCommunicationsPathAsync", []), new("GetCommunicationsPathAsync", []), new("GoOfflineAsync", [])],
            SourcePath: source, ExpectedSha256: hash, ControllerTargetId: "offline-fixture"));
        for (var i = 0; i < 400 && job.Status is "queued" or "running"; i++) { await Task.Delay(500); job = service.SdkJob(job.Id); }
        var valid = job.Status == "succeeded" && !job.ControllerEffectsPossible && job.Steps[1].Value!.Value.GetString() == path &&
            hash == ProjectService.Hash(await File.ReadAllBytesAsync(source));
        var report = new { valid, sourceSha256 = hash, controllerOperationsEnabled = false, connectionApisInvoked = false, job };
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "test-results", "sdk-offline-path.json"), JsonSerializer.Serialize(report, ProjectService.Json));
        if (!valid) throw new Exception("Offline communications-path fixture failed: " + job.Error);
        Console.WriteLine("PASS: Offline Set/GetCommunicationsPath and GoOffline preserve the source; controller operations disabled.");
    }
}
