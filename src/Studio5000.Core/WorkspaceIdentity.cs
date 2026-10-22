namespace Studio5000.Core;

public static class WorkspaceIdentity
{
    public const string Name = "RockwellMCPServer";
    public static readonly string Version = typeof(WorkspaceIdentity).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
    public static readonly string PackageVersion = typeof(WorkspaceIdentity).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "RockwellPackageVersion").Value!;
    public static string AssemblyFileVersion => typeof(WorkspaceIdentity).Assembly.GetName().Version!.ToString();
}
