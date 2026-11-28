using System.Threading.Channels;

namespace Namotion.ZWave.Tests.Testing;

/// <summary>
/// The host's end of an in-memory serial line: reads what the fake stick pushes, writes go to the stick.
/// </summary>
internal sealed class FakeSerialStream(FakeZWaveStick stick) : Stream
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _offset;

    public bool IsDisposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void Push(byte[] bytes) => _incoming.Writer.TryWrite(bytes);

    public void EndOfStream() => _incoming.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current is null || _offset == _current.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            _incoming.Reader.TryRead(out _current);
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current!.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        stick.OnHostWrite(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (stick.ThrowsOnDispose)
        {
            throw new IOException("The fake serial port could not be closed.");
        }

        IsDisposed = true;
        _incoming.Writer.TryComplete();
        base.Dispose(disposing);
    }
}
