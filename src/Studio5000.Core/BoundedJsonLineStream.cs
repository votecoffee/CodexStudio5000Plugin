namespace Studio5000.Core;

// MCP stdio frames are newline-delimited JSON. Count bytes across arbitrary
// read boundaries before handing a frame to the SDK; do not split/rewrite JSON.
public sealed class BoundedJsonLineStream(Stream input, int maximumFrameBytes) : Stream
{
    private int frameBytes;
    public override bool CanRead => input.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private void Count(ReadOnlySpan<byte> bytes)
    {
        if (maximumFrameBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        foreach (var value in bytes)
        {
            if (++frameBytes > maximumFrameBytes) throw new IOException("MCP input frame exceeds its reviewed byte limit.");
            if (value == (byte)'\n') frameBytes = 0;
        }
    }
    public override int Read(byte[] buffer, int offset, int count)
    { var read = input.Read(buffer, offset, count); Count(buffer.AsSpan(offset, read)); return read; }
    public override int Read(Span<byte> buffer)
    { var read = input.Read(buffer); Count(buffer[..read]); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { var read = await input.ReadAsync(buffer, cancellationToken); Count(buffer.Span[..read]); return read; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
}
