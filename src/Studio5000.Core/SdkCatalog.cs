using System.Text.Json;

namespace Studio5000.Core;

// Frozen public surface of the installed 2.1.974 assembly. Native coverage checks detect drift.
public static class SdkCatalog
{
    public static readonly SdkSurface Surface = JsonSerializer.Deserialize<SdkSurface>(
        typeof(SdkCatalog).Assembly.GetManifestResourceStream("Studio5000.Core.SdkSurface.json")!, ProjectService.Json)!;
    public static readonly string[] ManagedMethods = ["AddEventHandler", "RemoveEventHandler", "ClearEventHandlers", "Dispose"];
    public static readonly SdkMethod[] Methods = Surface.Methods.Where(m => !ManagedMethods.Contains(m.Name))
        .GroupBy(m => m.Name).Select(g => g.First()).ToArray();
    public static readonly string[] Factories = ["OpenLogixProjectAsync", "CreateNewProjectAsync", "ConvertAsync", "UploadToNewProjectAsync"];
    public static readonly string[] ControllerMethods = ["GoOnlineAsync", "DownloadAsync", "UploadAsync", "UploadToNewProjectAsync",
        "ReadControllerModeAsync", "ChangeControllerModeAsync", "StoreImageOnSDCardAsync", "LoadImageFromSDCardAsync",
        "GenerateSafetySignatureAsync", "DeleteSafetySignatureAsync"];
    public static readonly string[] FileParameters = ["projectFilePath", "savePath", "filePath", "xmlFileToImport"];
    public static bool IsSecret(string name) => name.Contains("password", StringComparison.OrdinalIgnoreCase);
    public static SdkMethod Find(string name) => Methods.FirstOrDefault(m => m.Name == name)
        ?? throw new OperationException("sdk_method_unknown", "Method is not in the supported SDK surface: " + name);
    public static SdkMethodInfo[] Describe() => Methods.Select(m => new SdkMethodInfo(m.Name,
        Factories.Contains(m.Name) ? "factory" : m.IsStatic ? "query" : "project", Effect(m.Name),
        m.Parameters.Where(p => p.Type != "System.Threading.CancellationToken" && !p.Type.Contains("IOperationEvent")).ToArray())).ToArray();
    private static string Effect(string name) => ControllerMethods.Contains(name) ? "controller" :
        name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("Read", StringComparison.Ordinal) || name == "IsSafetyLockedAsync" ? "read" : "working_copy";
    public static bool NeedsController(SdkCall[] calls) => calls.Any(c => ControllerMethods.Contains(c.Method) ||
        c.Arguments.TryGetValue("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "Online");

    public static void ValidateCalls(SdkCall[] calls)
    {
        if (calls is null || calls.Length is < 1 or > 100) throw new OperationException("sdk_calls_invalid", "Supply 1–100 SDK calls.");
        var onlineRequested = false;
        for (var i = 0; i < calls.Length; i++)
        {
            var call = calls[i];
            if (call is null || call.Arguments is null) throw new OperationException("sdk_calls_invalid", "Each call requires a method and arguments object (empty is allowed).");
            var method = Find(call.Method);
            if (call.Method == "GoOnlineAsync") onlineRequested = true;
            if (call.Method == "GoOfflineAsync") onlineRequested = false;
            if (onlineRequested && call.Method == "PartialImportFromXmlFileAsync")
                throw new OperationException("sdk_offline_import_online", "PartialImportFromXmlFileAsync is an offline-only SDK API. Use the with-target or rungs import for an online session.");
            if (Factories.Contains(call.Method) && i != 0) throw new OperationException("sdk_factory_order", "A project factory must be the first call.");
            foreach (var arg in call.Arguments)
            {
                var parameter = method.Parameters.FirstOrDefault(p => p.Name == arg.Key);
                if (parameter is null || parameter.Type == "System.Threading.CancellationToken" || parameter.Type.Contains("IOperationEvent"))
                    throw new OperationException("sdk_argument_unknown", $"Unsupported argument {call.Method}.{arg.Key}.");
                ValidateValue(parameter, arg.Value);
            }
            foreach (var p in method.Parameters.Where(p => !p.Optional && !p.Type.Contains("IOperationEvent") && p.Type != "System.Threading.CancellationToken"))
                if (!call.Arguments.ContainsKey(p.Name) && !FileParameters.Contains(p.Name) && p.Name != "commPath" &&
                    !(call.Method is "ConvertAsync" or "SetCommunicationsPathAsync" && p.Name == "projectPath"))
                    throw new OperationException("sdk_argument_missing", $"Missing {call.Method}.{p.Name}.");
            // Never permit the SDK to overwrite user paths. Output names are remapped inside a private job.
            if (call.Method == "SaveAsAsync" && call.Arguments.TryGetValue("force", out var force) && force.GetBoolean())
                throw new OperationException("sdk_overwrite_denied", "force must be false; SDK outputs use new files.");
        }
    }

    public static void ValidateValue(SdkParameter p, JsonElement value)
    {
        try
        {
            if (p.Type.Contains('+'))
            {
                var definition = Surface.Enums.Single(e => e.Name == p.Type.Split('+')[1]);
                if (value.ValueKind != JsonValueKind.String || !definition.Values.Contains(value.GetString())) throw new FormatException();
                return;
            }
            _ = p.Type switch
            {
                "System.String" => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException(),
                "System.Boolean" => value.GetBoolean().ToString(),
                "System.SByte" => value.GetSByte().ToString(), "System.Byte" => value.GetByte().ToString(),
                "System.Int16" => value.GetInt16().ToString(), "System.UInt16" => value.GetUInt16().ToString(),
                "System.Int32" => value.GetInt32().ToString(), "System.UInt32" => value.GetUInt32().ToString(),
                "System.Int64" => value.GetInt64().ToString(), "System.UInt64" => value.GetUInt64().ToString(),
                "System.Single" => float.IsFinite(value.GetSingle()) ? "finite" : throw new FormatException(),
                "System.Double" => double.IsFinite(value.GetDouble()) ? "finite" : throw new FormatException(),
                "System.Byte[]" => value.GetBytesFromBase64().Length <= 30000 ? "bytes" : throw new FormatException(),
                _ => throw new FormatException()
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        { throw new OperationException("sdk_argument_invalid", $"Invalid {p.Name}: expected {p.Type}. Use exact enum names; raw bytes use base64 (maximum 30000 bytes)."); }
    }
}
