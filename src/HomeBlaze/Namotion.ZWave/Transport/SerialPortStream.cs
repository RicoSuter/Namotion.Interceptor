using System.IO.Ports;

namespace Namotion.ZWave.Transport;

/// <summary>
/// The base stream of an open serial port that closes the port when it is disposed.
/// </summary>
internal sealed class SerialPortStream(SerialPort port) : Stream
{
    private readonly Stream _stream = port.BaseStream;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stream.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stream.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _stream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            port.Dispose();
        }

        base.Dispose(disposing);
    }
}
