using System.Diagnostics;
using Studio5000.Core;
using Studio5000.Core.Resources;

if (args is ["--version"]) { Console.WriteLine(WorkspaceIdentity.Version); return 0; }
// This is a surviving transport owner, not a register-and-exit helper. The
// SDK still creates/serves its ordinary stdio connection through raw byte pipes.
var root = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT") ?? Directory.GetCurrentDirectory());
if (args is ["--offline-bootstrap-preview"])
{
    var previewLimits = McpClientLimits.Read(root);
    var ledger = new ResourceLedger(Environment.GetEnvironmentVariable("ROCKWELL_RESOURCE_REGISTRY") ?? WindowsResourceProcessDriver.DefaultRegistry(),
        new WindowsResourceProcessDriver(), previewLimits.MaximumOwnedResources);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(OfflineServiceBootstrap.PreviewRecovery(root, AppContext.BaseDirectory,
        ledger.Owner("rockwell-mcp-server")), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    return 0;
}
if (args.Length != 0) throw new ArgumentException("The stdio owner accepts only its fixed read-only preview or no arguments.");
if (McpFrontdoorPolicy.Read(AppContext.BaseDirectory) is { } frontdoor)
{
    await Studio5000.Mcp.McpApplication.RunAsync(args, frontdoor);
    return 0;
}
if (File.Exists(Path.Combine(root, ".local", "maintenance-hold.json"))) throw new InvalidOperationException("Operator maintenance hold blocks MCP startup.");
var limits = McpClientLimits.Read(root);
using var owners = ParentProcessOwners.Capture();
using var lifetime = new CancellationTokenSource();
var parent = owners.RequestShutdownOnExitAsync(lifetime.Cancel, lifetime.Token);
using var resources = new ResourceSupervisor(root, Environment.GetEnvironmentVariable("ROCKWELL_RESOURCE_REGISTRY") ?? WindowsResourceProcessDriver.DefaultRegistry(), limits.MaximumOwnedProcesses, limits.MaximumOwnedResources);
var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(resources.Owner.Identity.Executable)!, "Studio5000.Mcp.exe"))
{
    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
};
var recovered = McpFrontendRecovery.ReconcileClosed(resources.Owner, start.FileName);
if (recovered.Length != 0) Console.Error.WriteLine($"Reconciled {recovered.Length} fully exited Rockwell MCP transport claims; unresolved reservations remain retained.");
if (resources.Ledger.Records().Count(r => r.AppId == "rockwell-mcp-server" && !r.Terminal) >= limits.MaximumOwnedResources)
    OfflineServiceBootstrap.PrepareAdmission(root, AppContext.BaseDirectory, resources.Owner);
using var child = resources.LaunchStatelessFrontend(start);
var service = new OfflineServiceGate(root, AppContext.BaseDirectory, resources.Owner, lifetime.Token);
var readiness = Task.Run(async () =>
{
    try { await service.Maintain(lifetime.Token); }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    catch (Exception error) { Console.Error.WriteLine("Offline shared-service startup refused: " + error.Message); }
});
try
{
    return await OwnedStdioRelay.RunAsync(child, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError(), limits.ShutdownTimeoutSeconds, lifetime.Token);
}
finally
{
    lifetime.Cancel();
    try { await parent; } catch (OperationCanceledException) { }
    await readiness;
}
