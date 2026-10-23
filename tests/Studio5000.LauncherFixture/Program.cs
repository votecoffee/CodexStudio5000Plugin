using System.Text.Json;

// Harmless stdio stand-in only. It has no Core, SDK, service or network dependency.
var root = Path.GetFullPath(Environment.GetEnvironmentVariable("ROCKWELL_LAUNCHER_FIXTURE_ROOT") ?? throw new InvalidOperationException("Fixture root required."));
if (!File.Exists(Path.Combine(root, ".launcher-fixture"))) throw new InvalidOperationException("Explicit disposable marker required.");
File.WriteAllText(Path.Combine(root, "started.json"), JsonSerializer.Serialize(new
{
    cwd = Directory.GetCurrentDirectory(), appRoot = Environment.GetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT"), arguments = args,
    nativeCodeExecuted = false
}));
await Console.Error.WriteLineAsync("harmless launcher fixture");
await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
