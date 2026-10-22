using System.Text;
using System.Text.Json;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

// File/metadata tests only. Real EOF, ancestor loss and process-kill acceptance
// belongs to the isolated lifecycle profile; none is inferred from these tests.
public sealed class McpClientLifetimeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-mcp-limits-" + Guid.NewGuid().ToString("N"));
    public McpClientLifetimeTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(0, 10)] [InlineData(17, 10)] [InlineData(4, 0)] [InlineData(4, 31)]
    public void Invalid_limits_are_rejected_before_admission(int maximum, int timeout) =>
        Assert.Throws<ArgumentException>(() => new McpClientLimits(maximum, timeout).Validate());

    [Fact]
    public void Defaults_and_gui_settings_are_bounded_without_connecting_to_a_service()
    {
        Assert.Equal(new McpClientLimits(4, 10), McpClientLimits.Read(root));
        File.WriteAllText(Path.Combine(root, "service.local.json"), "{\"Studio5000\":{\"MaximumMcpClients\":6,\"McpShutdownTimeoutSeconds\":12}}", new UTF8Encoding(true));
        Assert.Equal(new McpClientLimits(6, 12), McpClientLimits.Read(root));
        File.WriteAllText(Path.Combine(root, "service.local.json"), "{\"Studio5000\":{\"MaximumMcpClients\":4,\"maximumMcpClients\":8}}");
        Assert.Throws<ArgumentException>(() => McpClientLimits.Read(root));
        File.WriteAllText(Path.Combine(root, "service.local.json"), "{\"Studio5000\":{\"MaximumMcpClients\":17}}");
        Assert.Throws<ArgumentException>(() => McpClientLimits.Read(root));
        File.WriteAllText(Path.Combine(root, "service.local.json"), new string(' ', 1024 * 1024 + 1));
        Assert.Throws<ArgumentException>(() => McpClientLimits.Read(root));
    }

    [Fact]
    public void Capacity_is_owned_by_handles_and_disposal_releases_a_slot_without_pid_cleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var first = McpClientLease.Acquire(root, new(2));
        using var second = McpClientLease.Acquire(root, new(2));
        Assert.NotEqual(first.Slot, second.Slot);
        Assert.Throws<InvalidOperationException>(() => McpClientLease.Acquire(root, new(2)));
        first.Dispose(); first.Dispose();
        using var replacement = McpClientLease.Acquire(root, new(2));
        Assert.Equal(first.Slot, replacement.Slot);
        Assert.Equal(17, Directory.GetFiles(Path.Combine(root, ".local", "mcp-clients")).Length);
    }

    [Fact]
    public void Lowering_a_limit_counts_higher_slots_and_preserves_other_clients()
    {
        if (!OperatingSystem.IsWindows()) return;
        var leases = Enumerable.Range(0, 6).Select(_ => McpClientLease.Acquire(root, new(8))).ToArray();
        try
        {
            leases[0].Dispose(); leases[1].Dispose();
            Assert.Throws<InvalidOperationException>(() => McpClientLease.Acquire(root, new(3)));
            leases[2].Dispose(); leases[3].Dispose();
            using var admitted = McpClientLease.Acquire(root, new(3));
            Assert.Equal(0, admitted.Slot);
            Assert.Throws<InvalidOperationException>(() => McpClientLease.Acquire(root, new(3)));
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public void Stale_metadata_is_not_an_active_lease_or_authority_to_stop_a_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, ".local", "mcp-clients"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "slot-0.lock"), JsonSerializer.Serialize(new { pid = 1, startedUtc = "old" }));
        using var admitted = McpClientLease.Acquire(root, new(1));
        Assert.Equal(0, admitted.Slot);
    }

    [Fact]
    public void Shared_settings_validator_rejects_excessive_frontend_limits()
    {
        Assert.Throws<ArgumentException>(() => ServiceSettingsStore.ResolveAndValidate(new() { MaximumMcpClients = 99 }, root));
        Assert.Throws<ArgumentException>(() => ServiceSettingsStore.ResolveAndValidate(new() { McpShutdownTimeoutSeconds = 99 }, root));
    }

    [Theory]
    [InlineData("null")] [InlineData("true")] [InlineData("\"4\"")] [InlineData("4.5")] [InlineData("[]")]
    public void Noninteger_limits_are_rejected_without_opening_an_admission_slot(string value)
    {
        File.WriteAllText(Path.Combine(root, "service.local.json"), "{\"Studio5000\":{\"MaximumMcpClients\":" + value + "}}");
        Assert.Throws<ArgumentException>(() => McpClientLimits.Read(root));
        Assert.False(Directory.Exists(Path.Combine(root, ".local", "mcp-clients")));
    }
}
