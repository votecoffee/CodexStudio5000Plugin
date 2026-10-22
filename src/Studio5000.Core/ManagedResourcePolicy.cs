namespace Studio5000.Core;

public static class ManagedResourcePolicy
{
    public const string OwnedProfile = Resources.ManagedServiceAdmission.Profile;

    // Native/controller authorization stays in the shared service. Managed
    // startup additionally requires the owner's durable admission handshake.
    public static void Validate(string? profile, ServiceOptions options)
    {
        if (profile != OwnedProfile) throw new ArgumentException("The managed native resource ownership profile is required; legacy offline-only generations cannot be adopted.");
        OwnedProcessContainment.ValidateLimit(options.MaximumOwnedProcesses);
    }
}
