namespace Studio5000.Core;

public static class ManagedResourcePolicy
{
    public const string OfflineProfile = "offline-contained-v1";

    // The source Python supervisor currently covers only the inherited offline
    // tree. Recheck effective options here to reject a settings/startup race.
    public static void Validate(string? profile, ServiceOptions options)
    {
        if (profile != OfflineProfile) throw new ArgumentException("A reviewed managed resource profile is required.");
        if (options.NativeBridgePath is not null || options.CipBridgePath is not null ||
            options.EnableControllerOperations || options.ControllerTargets.Length != 0)
            throw new ArgumentException("native_resource_binding_required: managed native processes require an app-owned durable registration/recovery adapter; this profile is offline only.");
    }
}
