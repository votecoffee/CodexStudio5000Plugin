using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class AtomicMetadataReplacementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-metadata-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly string source, target;
    public AtomicMetadataReplacementTests()
    {
        Directory.CreateDirectory(root); source = Path.Combine(root, "pending"); target = Path.Combine(root, "receipt");
        File.WriteAllText(source, "exact-completed-metadata"); File.WriteAllText(target, "dispatch-intent");
    }
    public void Dispose() => Directory.Delete(root, true);
    private static IOException Error(int code) => new WindowsFailure(code);
    private sealed class WindowsFailure : IOException
    { public WindowsFailure(int code) : base("Synthetic Windows replacement failure") => HResult = unchecked((int)0x80070000) | code; }
    [Theory]
    [InlineData(32)] [InlineData(33)] [InlineData(1175)]
    public void Known_transient_failure_republishes_the_same_bytes_without_an_action_callback(int code)
    {
        var calls = 0; var delays = new List<int>();
        AtomicMetadataReplacement.Replace(source, target, (s, t) =>
        {
            Assert.Equal("exact-completed-metadata", File.ReadAllText(s)); Assert.Equal("dispatch-intent", File.ReadAllText(t));
            if (++calls < 3) throw Error(code);
            File.Replace(s, t, null);
        }, delays.Add);
        Assert.Equal(3, calls); Assert.Equal(new[] { 25, 50 }, delays); Assert.False(File.Exists(source));
        Assert.Equal("exact-completed-metadata", File.ReadAllText(target));
    }
    [Theory]
    [InlineData(1175, 7)] [InlineData(1176, 1)] [InlineData(1177, 1)] [InlineData(5, 1)]
    public void Exhaustion_or_ambiguous_failure_is_not_reconciled_or_redispatched(int code, int expected)
    {
        var calls = 0;
        Assert.Throws<WindowsFailure>(() => AtomicMetadataReplacement.Replace(source, target, (_, _) => { calls++; throw Error(code); }, _ => { }));
        Assert.Equal(expected, calls); Assert.Equal("dispatch-intent", File.ReadAllText(target)); Assert.True(File.Exists(source));
    }
    [Fact]
    public void A_changed_name_disables_retry_even_for_a_normally_transient_error()
    {
        var calls = 0;
        Assert.Throws<WindowsFailure>(() => AtomicMetadataReplacement.Replace(source, target, (s, _) => { calls++; File.Delete(s); throw Error(1175); }, _ => { }));
        Assert.Equal(1, calls); Assert.Equal("dispatch-intent", File.ReadAllText(target));
    }
}
