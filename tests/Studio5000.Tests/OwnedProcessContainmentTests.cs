using System.Reflection;
using System.Runtime.InteropServices;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

// No job is entered and no child is launched by these pure policy tests.
public sealed class OwnedProcessContainmentTests
{
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(129)]
    public void Invalid_owned_process_caps_are_rejected_by_the_shared_store(int maximum)
    {
        Assert.Throws<ArgumentException>(() => ServiceSettingsStore.ResolveAndValidate(
            new() { MaximumOwnedProcesses = maximum }, Path.GetTempPath()));
    }

    [Fact]
    public void Snapshot_without_entry_does_not_claim_containment_or_recovery()
    {
        var status = OwnedProcessContainment.Snapshot();
        Assert.False(status.Enabled);
        Assert.Null(status.MaximumProcesses);
        Assert.Null(status.ActiveProcesses);
        Assert.Equal("unknown", status.ExternalNativeEffects);
        Assert.Equal("not-integrated", status.DurableResourceRecovery);
    }

    [Fact]
    public void Native_structure_layout_matches_the_windows_abi_without_entering_a_job()
    {
        int Size(string name) => Marshal.SizeOf(typeof(OwnedProcessContainment).GetNestedType(name, BindingFlags.NonPublic)!);
        Assert.Equal(IntPtr.Size == 8 ? 64 : 48, Size("BasicLimits"));
        Assert.Equal(48, Size("IoCounters"));
        Assert.Equal(IntPtr.Size == 8 ? 144 : 112, Size("ExtendedLimits"));
        Assert.Equal(48, Size("Accounting"));
    }

    [Fact]
    public void Native_gui_launch_refuses_before_any_executable_or_process_observation()
    {
        var root = Path.Combine(Path.GetTempPath(), "rockwell-launch-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new ProjectService(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
            var error = Assert.Throws<OperationException>(() => service.LaunchFactoryTalkStudio());
            Assert.Equal("native_gui_ownership_required", error.Code);
            Assert.Contains("Launch Studio manually", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }
}
