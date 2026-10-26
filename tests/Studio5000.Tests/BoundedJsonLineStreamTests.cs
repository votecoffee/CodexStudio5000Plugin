using System.Text;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class BoundedJsonLineStreamTests
{
    [Fact]
    public async Task Split_reads_preserve_UTF8_and_reset_only_at_physical_newlines()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"x\":\"ü\"}\r\n{}\n");
        using var stream = new BoundedJsonLineStream(new MemoryStream(bytes), 12);
        using var output = new MemoryStream(); var buffer = new byte[2]; int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory())) != 0) output.Write(buffer, 0, read);
        Assert.Equal(bytes, output.ToArray());
    }
    [Fact]
    public async Task Oversized_partial_frame_is_rejected_before_returning_it_to_the_parser()
    {
        using var stream = new BoundedJsonLineStream(new MemoryStream("12345678"u8.ToArray()), 6);
        Assert.Equal(4, stream.Read(new byte[4]));
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadAsync(new byte[4].AsMemory()));
    }
    [Fact]
    public void Multiple_small_frames_are_not_counted_as_one_large_frame()
    {
        var bytes = "{}\n{}\n{}\n"u8.ToArray();
        using var stream = new BoundedJsonLineStream(new MemoryStream(bytes), 3);
        var buffer = new byte[bytes.Length]; Assert.Equal(bytes.Length, stream.Read(buffer, 0, buffer.Length)); Assert.Equal(bytes, buffer);
    }
}
