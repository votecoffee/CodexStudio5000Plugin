using System.Diagnostics;
using Studio5000.Core;
using Studio5000.Core.Resources;

if (args is ["--version"]) { Console.WriteLine(WorkspaceIdentity.Version); return 0; }
// This is a surviving transport owner, not a register-and-exit helper. The
// SDK still creates/serves its ordinary stdio connection through raw byte pipes.
if (args.Length != 0) throw new ArgumentException("The stdio owner accepts no executable or arbitrary command arguments.");
var root = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT") ?? Directory.GetCurrentDirectory());
if (File.Exists(Path.Combine(root, ".local", "maintenance-hold.json"))) throw new InvalidOperationException("Operator maintenance hold blocks MCP startup.");
var limits = McpClientLimits.Read(root);
using var owners = ParentProcessOwners.Capture();
using var lifetime = new CancellationTokenSource();
var parent = owners.RequestShutdownOnExitAsync(lifetime.Cancel, lifetime.Token);
using var resources = new ResourceSupervisor(root, Environment.GetEnvironmentVariable("ROCKWELL_RESOURCE_REGISTRY") ?? WindowsResourceProcessDriver.DefaultRegistry(), limits.MaximumOwnedProcesses, limits.MaximumOwnedResources);
var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Studio5000.Mcp.exe"))
{
    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
};
using var child = resources.Launch(start, "Rockwell MCP stdio adapter", "mcp-adapter");
try
{
    return await OwnedStdioRelay.RunAsync(child, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError(), limits.ShutdownTimeoutSeconds, lifetime.Token);
}
finally
{
    lifetime.Cancel();
    try { await parent; } catch (OperationCanceledException) { }
}
