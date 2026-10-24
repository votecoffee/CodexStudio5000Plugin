using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class ResourceLockWaitTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void Only_transient_lock_acquisition_is_retried(int error)
    {
        var attempts = 0; var operations = 0;
        ResourceLedger.WaitForLock(() =>
        {
            if (++attempts < 3) throw new IOException("Synthetic lock contention", unchecked((int)0x80070000) | error);
        }, TimeSpan.FromSeconds(1));
        operations++;
        Assert.Equal(3, attempts); Assert.Equal(1, operations);
    }
    [Theory]
    [InlineData(5)]
    [InlineData(87)]
    [InlineData(1175)]
    public void Other_io_errors_are_not_retried(int error)
    {
        var attempts = 0;
        Assert.Throws<IOException>(() => ResourceLedger.WaitForLock(() =>
        { attempts++; throw new IOException("Non-lock I/O error", unchecked((int)0x80070000) | error); }, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, attempts);
    }
    [Fact]
    public void Exhausted_acquisition_never_dispatches_metadata_action()
    {
        var attempts = 0; var dispatched = false;
        Assert.Throws<IOException>(() =>
        {
            ResourceLedger.WaitForLock(() => { attempts++; throw new IOException("Held lock", unchecked((int)0x80070021)); }, TimeSpan.Zero);
            dispatched = true;
        });
        Assert.Equal(1, attempts); Assert.False(dispatched);
    }
}
