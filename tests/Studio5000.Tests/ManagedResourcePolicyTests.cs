using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ManagedResourcePolicyTests
{
    [Fact]
    public void Native_owned_profile_is_valid_without_starting_any_resource() =>
        ManagedResourcePolicy.Validate(ManagedResourcePolicy.OwnedProfile, new());

    [Theory]
    [InlineData(null)] [InlineData("unknown")] [InlineData("offline-contained-v1")]
    public void Unreviewed_profile_cannot_skip_ownership_checks(string? profile) =>
        Assert.Throws<ArgumentException>(() => ManagedResourcePolicy.Validate(profile, new()));

    [Fact]
    public void Native_features_remain_available_in_the_owned_profile_without_changing_authorization()
    {
        foreach (var options in new ServiceOptions[] {
            new() { NativeBridgePath = "native.exe" }, new() { CipBridgePath = "cip.exe" },
            new() { EnableControllerOperations = true }, new() { ControllerTargets = [new("fixture", "explicit-route")] } })
        {
            ManagedResourcePolicy.Validate(ManagedResourcePolicy.OwnedProfile, options);
        }
    }
}
