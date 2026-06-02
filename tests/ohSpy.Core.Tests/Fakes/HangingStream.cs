namespace ohSpy.Core.Tests.Fakes;

/// <summary>
/// A <see cref="Stream"/> whose <c>ReadAsync</c> blocks indefinitely (until cancelled).
/// Use to simulate the "HTTP headers arrived, body hangs" failure mode that
/// motivated AC-3.5 (the prior tool's actual defect).
/// </summary>
internal sealed class HangingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return 0; // unreachable; ct will cancel
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
}
