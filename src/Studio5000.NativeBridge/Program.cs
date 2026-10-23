using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using Studio5000.NativeBridge;
using Studio5000.Core.Resources;
using Studio5000.Core;

var profile = Environment.GetEnvironmentVariable(NativeExecutionPolicy.EnvironmentKey) ?? NativeExecutionPolicy.Standard;
NativeExecutionPolicy.ValidateName(profile);
if (profile == NativeExecutionPolicy.LocalProject && args.Length != 0 && args[0] is "--watch" or "--create-fixture")
    throw new OperationException("native_local_operation_denied", "Use bounded discrete SDK jobs in local-project; direct fixture/watch entry points are disabled.");

if (args.Length == 1 && args[0] == "--sdk-coverage")
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SdkRunner.Coverage(), Studio5000.Core.ProjectService.Json));
    return 0;
}
if (args.Length == 2 && args[0] == "--sdk") return await SdkRunner.RunAsync(args[1]);
if (args.Length == 1 && args[0] == "--watch") return await SdkRunner.RunWatchAsync();

// Offline project creation is also available for reproducible native test fixtures.
if (args.Length == 2 && args[0] == "--create-fixture")
{
    try
    {
        var path = Path.GetFullPath(args[1]);
        if (File.Exists(path) || !Path.GetExtension(path).Equals(".acd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Fixture output must be a new ACD filename.");
        using var fixture = await LogixProject.CreateNewProjectAsync(path, 35, "1756-L81E", "CodexDemo", new StdOutEventLogger());
        await fixture.SaveAsync();
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}
// Compatibility conversion entry point; structured SDK operations use --sdk and stdin.
if (args.Length != 2) { Console.Error.WriteLine("Usage: Studio5000.NativeBridge <input.acd|l5x> <new-output.acd|l5x>"); return 2; }
try
{
    using var lifecycle = NativeLifecycleJournal.FromEnvironment();
    var input = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]);
    if (!File.Exists(input) || File.Exists(output) ||
        !new[] { ".acd", ".l5x" }.Contains(Path.GetExtension(input).ToLowerInvariant()) ||
        !new[] { ".acd", ".l5x" }.Contains(Path.GetExtension(output).ToLowerInvariant()))
        throw new ArgumentException("Input must exist; output must be a new ACD/L5X path.");
    using (LogixProject project = await LogixProject.OpenLogixProjectAsync(input, new StdOutEventLogger()))
    {
        if (profile == NativeExecutionPolicy.LocalProject)
            NativeExecutionPolicy.ValidateProjectConnection(profile, (await project.ReadConnectedStateAsync()).ToString());
        await project.SaveAsAsync(output, false);
    }
    if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("Native SDK returned an empty result.");
    lifecycle?.Complete(true, true, true);
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
