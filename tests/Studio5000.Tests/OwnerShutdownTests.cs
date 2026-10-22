using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Studio5000.Core;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class OwnerShutdownTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-owner-stop-" + Guid.NewGuid().ToString("N"));
    private readonly string actor = new('a', 32), key = new('k', 64);
    public OwnerShutdownTests() => Directory.CreateDirectory(Path.Combine(root, ".local"));
    public void Dispose() => Directory.Delete(root, true);
    private void Request(string signature = "valid", string? owner = null)
    {
        owner ??= actor; var request = new string('c', 32);
        var body = "rockwell-stop-v1\n" + owner + "\n" + owner + "\n" + request;
        var signed = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(root, ".local", "service-owner-stop.json"), JsonSerializer.Serialize(new { schemaVersion = 1, ownerId = owner, resourceId = owner, requestId = request, signature = signature == "valid" ? signed : signature }));
    }
    [Fact]
    public async Task Only_exact_generation_and_authentication_can_dispatch_close()
    {
        var mailbox = new OwnerShutdownMailbox(root, actor, key); var calls = 0;
        Task<OwnerShutdownDecision> Close() { calls++; return Task.FromResult(new OwnerShutdownDecision(true, "Synthetic resources closed")); }
        Request(new string('0', 64)); Assert.False(await mailbox.PollAsync(Close, default));
        Request(owner: new('b', 32)); Assert.False(await mailbox.PollAsync(Close, default));
        Assert.Equal(0, calls);
        Request(); Assert.True(await mailbox.PollAsync(Close, default));
        Assert.False(await mailbox.PollAsync(Close, default)); Assert.Equal(1, calls);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(root, ".local", "service-owner-stop.json")));
    }
    [Fact]
    public async Task A_refusal_keeps_the_owner_running_and_is_not_automatically_retried()
    {
        Request(); var mailbox = new OwnerShutdownMailbox(root, actor, key); var calls = 0;
        Task<OwnerShutdownDecision> Refuse() { calls++; return Task.FromResult(new OwnerShutdownDecision(false, "Native document dirty")); }
        Assert.False(await mailbox.PollAsync(Refuse, default)); Assert.False(await mailbox.PollAsync(Refuse, default)); Assert.Equal(1, calls);
        using var reply = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ".local", "service-owner-stop-result.json")));
        Assert.Equal("blocked", reply.RootElement.GetProperty("state").GetString());
    }
    [Fact]
    public async Task An_unknown_dispatch_result_remains_uncertain_and_never_replays()
    {
        Request(); var mailbox = new OwnerShutdownMailbox(root, actor, key); var calls = 0;
        Task<OwnerShutdownDecision> Lost() { calls++; throw new IOException("Synthetic lost close result"); }
        Assert.False(await mailbox.PollAsync(Lost, default)); Assert.False(await mailbox.PollAsync(Lost, default)); Assert.Equal(1, calls);
        Assert.False(await new OwnerShutdownMailbox(root, actor, key).PollAsync(Lost, default)); Assert.Equal(1, calls);
        using var reply = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ".local", "service-owner-stop-result.json")));
        Assert.Equal("uncertain", reply.RootElement.GetProperty("state").GetString());
    }
    [Fact]
    public async Task Console_stop_is_signed_once_and_does_not_retry_a_refused_close()
    {
        var mailbox = new OwnerShutdownMailbox(root, actor, key); var calls = 0;
        mailbox.RequestOnce(); var before = File.ReadAllText(Path.Combine(root, ".local", "service-owner-stop.json"));
        Task<OwnerShutdownDecision> Refuse() { calls++; return Task.FromResult(new OwnerShutdownDecision(false, "Synthetic dirty document")); }
        Assert.False(await mailbox.PollAsync(Refuse, default));
        mailbox.RequestOnce(); Assert.False(await mailbox.PollAsync(Refuse, default));
        Assert.Equal(1, calls); Assert.Equal(before, File.ReadAllText(Path.Combine(root, ".local", "service-owner-stop.json")));
    }
    [Fact]
    public async Task Concurrent_mailbox_instances_cannot_dispatch_the_same_intent_twice()
    {
        Request(); var calls = 0;
        async Task<OwnerShutdownDecision> Close() { Interlocked.Increment(ref calls); await Task.Delay(20); return new(true, "Synthetic close"); }
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new OwnerShutdownMailbox(root, actor, key).PollAsync(Close, default)));
        Assert.Single(replies, r => r); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Shared_project_fence_refuses_new_mutations_until_refusal_is_resolved()
    {
        var state = Path.Combine(root, "state");
        using var service = new ProjectService(new() { StateDirectory = state, AllowedRoots = [root] });
        Assert.True(service.TryFenceShutdown());
        var source = Path.Combine(root, "source.L5X");
        File.WriteAllText(source, "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"35.00\" TargetType=\"Controller\" ContainsContext=\"false\"><Controller Name=\"Fixture\"><Tags/><Programs/></Controller></RSLogix5000Content>");
        var error = await Assert.ThrowsAsync<OperationException>(() => service.RegisterAsync(new(source)));
        Assert.Equal("service_stopping", error.Code);
        service.ReleaseShutdownFence();
        Assert.NotNull(await service.RegisterAsync(new(source)));
    }
    private sealed class ReplacementFailure : IOException
    { public ReplacementFailure(int code) : base("Synthetic Windows replacement failure") => HResult = unchecked((int)0x80070000) | code; }
    private static IOException ReplaceError(int code) => new ReplacementFailure(code);
    [Theory]
    [InlineData(32)] [InlineData(33)] [InlineData(1175)]
    public async Task Temporary_receipt_replacement_failures_do_not_repeat_resource_close(int code)
    {
        Request(); var attempts = 0; var closes = 0;
        var mailbox = new OwnerShutdownMailbox(root, actor, key, (source, target) =>
        {
            if (++attempts <= 2) throw ReplaceError(code);
            File.Replace(source, target, null);
        });
        Task<OwnerShutdownDecision> Close() { closes++; return Task.FromResult(new OwnerShutdownDecision(true, "Synthetic closed")); }
        Assert.True(await mailbox.PollAsync(Close, default)); Assert.Equal(1, closes); Assert.Equal(4, attempts);
        Assert.False(await mailbox.PollAsync(Close, default)); Assert.Equal(1, closes);
        using var reply = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ".local", "service-owner-stop-result.json")));
        Assert.Equal("completed", reply.RootElement.GetProperty("state").GetString());
    }
    [Theory]
    [InlineData(1175, 7)] [InlineData(1176, 1)] [InlineData(1177, 1)]
    public async Task Exhausted_or_ambiguous_receipt_failure_retains_claim_without_redispatch(int code, int expectedAttempts)
    {
        Request(); var attempts = 0; var closes = 0;
        var mailbox = new OwnerShutdownMailbox(root, actor, key, (_, _) => { attempts++; throw ReplaceError(code); });
        Task<OwnerShutdownDecision> Close() { closes++; return Task.FromResult(new OwnerShutdownDecision(true, "Synthetic closed")); }
        await Assert.ThrowsAnyAsync<IOException>(() => mailbox.PollAsync(Close, default));
        Assert.Equal(expectedAttempts, attempts); Assert.Equal(1, closes);
        Assert.False(await new OwnerShutdownMailbox(root, actor, key).PollAsync(Close, default)); Assert.Equal(1, closes);
        using var retained = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, ".local", "owner-stop-history", new string('c', 32) + ".json")));
        Assert.Equal("reviewing", retained.RootElement.GetProperty("state").GetString());
    }
}
