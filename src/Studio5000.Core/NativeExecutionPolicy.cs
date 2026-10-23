namespace Studio5000.Core;

// A local native project session is distinct from controller permission and
// from SCM/GUI ownership. Every caller uses this same policy before launch.
public static class NativeExecutionPolicy
{
    public const string Standard = "standard", LocalProject = "local-project";
    public const string EnvironmentKey = "ROCKWELL_NATIVE_EXECUTION_PROFILE";
    public static ServiceOptions CreateLocalOptions(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Use an explicit absolute candidate workspace.");
        root = Path.GetFullPath(root);
        var options = new ServiceOptions
        {
            NativeExecutionProfile = LocalProject, EnableNativeGuiLaunch = false,
            EnableSdkServiceRecovery = false, AutoStartSdkService = false, NativeTimeoutSeconds = 120,
            NativeBridgePath = Path.Combine(root, "artifacts", "app", "NativeBridge", "Studio5000.NativeBridge.exe"),
            StateDirectory = Path.Combine(root, ".local", "state"), AllowedRoots = [Path.Combine(root, "fixtures")],
            OutputRoots = [Path.Combine(root, "exports")], ExtensionsDirectory = Path.Combine(root, "extensions"),
            ExtensionWorkerPath = Path.Combine(root, "artifacts", "app", "ExtensionWorker", "Studio5000.ExtensionWorker.exe"),
            FactoryTalkSchemaDirectory = Path.Combine(root, ".local", "no-schemas"),
            FactoryTalkStudioPath = Path.Combine(root, ".local", "gui-disabled", "FactoryTalkStudio.exe"),
            ViewDesignerPath = Path.Combine(root, ".local", "gui-disabled", "ViewDesigner.exe"),
            MaximumConcurrentMutations = 1, MaximumQueuedMutations = 8, MaximumMcpClients = 2,
            MaximumOwnedProcesses = 8, MaximumOwnedResources = 16
        };
        ServiceSettingsStore.ResolveAndValidate(options, root);
        return options;
    }
    public static readonly string[] LocalMethods = ["OpenLogixProjectAsync", "CreateNewProjectAsync", "ConvertAsync",
        "GetProcessorTypesAsync", "SaveAsync", "SaveAsAsync", "BuildAsync", "PartialExportToXmlFileAsync",
        "PartialImportFromXmlFileAsync", .. SdkCatalog.Methods.Where(m => m.Name.StartsWith("GetTagValue", StringComparison.Ordinal)).Select(m => m.Name)];

    public static void ValidateOptions(ServiceOptions options)
    {
        ValidateName(options.NativeExecutionProfile);
        if (options.NativeExecutionProfile != LocalProject) return;
        if (options.EnableControllerOperations || options.AllowAnyControllerPath || options.ControllerTargets is not { Length: 0 } ||
            !string.IsNullOrWhiteSpace(options.CipBridgePath) || options.EnableSdkServiceRecovery || options.AutoStartSdkService || options.EnableNativeGuiLaunch ||
            options.AllowAnySourcePath || options.AllowAnyOutputPath)
            throw new ArgumentException("local-project requires restricted file roots, no controller targets/access, no CIP bridge, SDK-service recovery/preflight disabled and native GUI launch disabled. Change profiles explicitly before widening policy.");
    }
    public static void ValidateName(string profile)
    {
        if (profile is not (Standard or LocalProject)) throw new ArgumentException("NativeExecutionProfile must be standard or local-project.");
    }
    public static void ValidateCalls(string profile, SdkCall[] calls, string? communicationsPath = null, string? controllerTargetId = null)
    {
        ValidateName(profile);
        if (profile != LocalProject) return;
        if (communicationsPath is not null || controllerTargetId is not null || calls.Any(c => !LocalMethods.Contains(c.Method)) || SdkCatalog.NeedsController(calls))
            throw new OperationException("native_local_operation_denied", "local-project permits only its declared offline project methods, without any controller target or communications path.");
        foreach (var call in calls.Where(c => c.Method.StartsWith("GetTagValue", StringComparison.Ordinal)))
            if (!call.Arguments.TryGetValue("mode", out var mode) || mode.ValueKind != System.Text.Json.JsonValueKind.String || mode.GetString() != "Offline")
                throw new OperationException("native_local_mode_required", "local-project tag reads require explicit mode Offline.");
    }
    public static void ValidateWorkerProfile(string profile, string? ownerProfile)
    {
        ValidateName(profile);
        if (ownerProfile is not null && ownerProfile != profile)
            throw new OperationException("native_profile_mismatch", "Native worker profile differs from the exact owner's launch profile.");
    }
    public static void RequireGui(ServiceOptions options)
    {
        if (!options.EnableNativeGuiLaunch || options.NativeExecutionProfile == LocalProject)
            throw new OperationException("native_gui_disabled", "Native GUI launch is disabled by this service profile. Export-file operations remain available.");
    }
    public static void ValidateProjectConnection(string profile, string connectedState)
    {
        ValidateName(profile);
        if (profile == LocalProject && connectedState != "Offline")
            throw new OperationException("native_local_connected", "A local-project SDK session must remain Offline. No controller action or forced GoOffline was dispatched; inspect the retained session.");
    }
}
