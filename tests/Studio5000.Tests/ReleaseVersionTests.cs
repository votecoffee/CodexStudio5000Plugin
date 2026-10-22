using System.Reflection;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ReleaseVersionTests
{
    [Fact]
    public void Compiled_display_package_and_file_versions_share_one_timestamp()
    {
        var display = WorkspaceIdentity.Version;
        Assert.Matches("^[0-9]{2}\\.[0-9]{4}\\.[0-9]{6}$", display);
        Assert.Equal(Version.Parse(display), Version.Parse(WorkspaceIdentity.PackageVersion));
        var pieces = display.Split('.'); var time = pieces[2];
        var assembly = new Version(int.Parse(pieces[0]), int.Parse(pieces[1]), int.Parse(time[..4]), int.Parse(time[4..]));
        Assert.Equal(assembly.ToString(), WorkspaceIdentity.AssemblyFileVersion);
        Assert.Equal(assembly.ToString(), typeof(WorkspaceIdentity).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal(display, typeof(WorkspaceIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
    }
}
