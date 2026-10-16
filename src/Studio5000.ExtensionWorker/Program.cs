using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Studio5000.Extensibility;

if (args.Length != 3) throw new ArgumentException("Expected assembly path, entry type and workspace root.");
var assemblyPath = Path.GetFullPath(args[0]);
var output = Console.Out; Console.SetOut(Console.Error); // Includes assembly initialization/constructor diagnostics.
var load = new ExtensionLoadContext(assemblyPath);
var assembly = load.LoadFromAssemblyPath(assemblyPath);
var extension = Activator.CreateInstance(assembly.GetType(args[1], throwOnError: true)!) as IWorkspaceExtension
    ?? throw new ArgumentException("Entry type must implement IWorkspaceExtension.");
var context = new ExtensionContext(Path.GetDirectoryName(assemblyPath)!, Path.GetFullPath(args[2]));
while (await Console.In.ReadLineAsync() is { } line)
{
    WorkerReply reply;
    try
    {
        if (line.Length > 2 * 1024 * 1024) throw new ArgumentException("Request exceeds 2 MiB.");
        var request = JsonSerializer.Deserialize<WorkerRequest>(line, ExtensionProtocol.Json)!;
        var value = request.Method switch
        {
            "describe" => JsonSerializer.SerializeToElement(extension.Describe(), ExtensionProtocol.Json),
            "invoke" => await extension.InvokeAsync(request.Operation!, request.Arguments!.Value, context, CancellationToken.None),
            _ => throw new ArgumentException("Unknown worker request.")
        };
        reply = new(true, value);
    }
    catch (Exception ex) { reply = new(false, Error: ex.GetBaseException().Message); }
    var json = JsonSerializer.Serialize(reply, ExtensionProtocol.Json);
    if (json.Length > 2 * 1024 * 1024) json = JsonSerializer.Serialize(new WorkerReply(false, Error: "Result exceeds 2 MiB."), ExtensionProtocol.Json);
    await output.WriteLineAsync(json); await output.FlushAsync();
}
if (extension is IDisposable disposable) disposable.Dispose();

sealed class ExtensionLoadContext(string path) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver = new(path);
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IWorkspaceExtension).Assembly.GetName().Name) return typeof(IWorkspaceExtension).Assembly;
        var resolved = resolver.ResolveAssemblyToPath(name);
        return resolved is null ? null : LoadFromAssemblyPath(resolved);
    }
    protected override nint LoadUnmanagedDll(string name)
    { var resolved = resolver.ResolveUnmanagedDllToPath(name); return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved); }
}
