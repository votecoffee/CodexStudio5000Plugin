using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

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
// Intentionally limited to offline Open/SaveAs. No controller connectivity API is called here.
if (args.Length != 2) { Console.Error.WriteLine("Usage: Studio5000.NativeBridge <input.acd|l5x> <new-output.acd|l5x>"); return 2; }
try
{
    var input = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]);
    if (!File.Exists(input) || File.Exists(output) ||
        !new[] { ".acd", ".l5x" }.Contains(Path.GetExtension(input).ToLowerInvariant()) ||
        !new[] { ".acd", ".l5x" }.Contains(Path.GetExtension(output).ToLowerInvariant()))
        throw new ArgumentException("Input must exist; output must be a new ACD/L5X path.");
    using LogixProject project = await LogixProject.OpenLogixProjectAsync(input, new StdOutEventLogger());
    await project.SaveAsAsync(output, false);
    if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("Native SDK returned an empty result.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
