using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ManagedResourcePolicyTests
{
    [Fact]
    public void Offline_profile_is_valid_without_starting_any_resource() =>
        ManagedResourcePolicy.Validate(ManagedResourcePolicy.OfflineProfile, new());

    [Theory]
    [InlineData(null)] [InlineData("unknown")] [InlineData("native-owned-v1")]
    public void Unreviewed_profile_cannot_skip_ownership_checks(string? profile) =>
        Assert.Throws<ArgumentException>(() => ManagedResourcePolicy.Validate(profile, new()));

    [Fact]
    public void Effective_native_settings_are_rechecked_before_service_initialization()
    {
        foreach (var options in new ServiceOptions[] {
            new() { NativeBridgePath = "native.exe" }, new() { CipBridgePath = "cip.exe" },
            new() { EnableControllerOperations = true }, new() { ControllerTargets = [new("fixture", "explicit-route")] } })
        {
            var error = Assert.Throws<ArgumentException>(() => ManagedResourcePolicy.Validate(ManagedResourcePolicy.OfflineProfile, options));
            Assert.Contains("native_resource_binding_required", error.Message);
        }
    }
}
