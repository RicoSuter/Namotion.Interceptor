namespace Namotion.ZWave.SerialApi;

/// <summary>
/// Turns the bytes of the serial line into frames. Not thread-safe: one reader feeds and reads it.
/// </summary>
internal sealed class FrameParser
{
    // Type, function and checksum.
    private const int MinimumLength = 3;

    private byte[] _buffer = new byte[256];
    private int _start;
    private int _count;
    private long _position;

    /// <summary>
    /// Whether the unread bytes start with a start of frame byte whose frame is incomplete. Valid after <see cref="TryRead"/> returned <c>false</c>.
    /// </summary>
    public bool HasPartialFrame => _count > 0 && _buffer[_start] == FrameHeader.StartOfFrame;

    /// <summary>
    /// The number of bytes consumed since the parser was created: the position of the next unread byte on the line.
    /// </summary>
    public long Position => _position;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_start + _count + data.Length > _buffer.Length)
        {
            var required = _count + data.Length;
            var target = required > _buffer.Length ? new byte[Math.Max(required, _buffer.Length * 2)] : _buffer;
            Buffer.BlockCopy(_buffer, _start, target, 0, _count);
            _buffer = target;
            _start = 0;
        }

        data.CopyTo(_buffer.AsSpan(_start + _count));
        _count += data.Length;
    }

    public bool TryRead(out ParsedFrame frame)
    {
        while (_count > 0)
        {
            switch (_buffer[_start])
            {
                case FrameHeader.Ack:
                    Consume(1);
                    frame = new ParsedFrame(FrameKind.Ack, default);
                    return true;

                case FrameHeader.Nak:
                    Consume(1);
                    frame = new ParsedFrame(FrameKind.Nak, default);
                    return true;

                case FrameHeader.Can:
                    Consume(1);
                    frame = new ParsedFrame(FrameKind.Can, default);
                    return true;

                case FrameHeader.StartOfFrame:
                    if (_count < 2)
                    {
                        frame = default;
                        return false;
                    }

                    var length = _buffer[_start + 1];
                    if (length < MinimumLength)
                    {
                        Consume(1);
                        continue;
                    }

                    var total = length + 2;
                    if (_count < total)
                    {
                        frame = default;
                        return false;
                    }

                    var bytes = _buffer.AsSpan(_start, total);
                    if (FrameEncoder.ComputeChecksum(bytes[1..^1]) != bytes[^1])
                    {
                        Consume(total);
                        frame = new ParsedFrame(FrameKind.ChecksumError, default);
                        return true;
                    }

                    frame = new ParsedFrame(FrameKind.Data, new DataFrame((FrameType)bytes[2], bytes[3], bytes[4..^1].ToArray()));
                    Consume(total);
                    return true;

                default:
                    // A host waiting for traffic ignores every byte other than SOF, ACK, NAK and CAN (INS12350).
                    Consume(1);
                    continue;
            }
        }

        frame = default;
        return false;
    }

    /// <summary>
    /// Drops the start of frame byte of the partial frame, so that the bytes after it are parsed again.
    /// </summary>
    public void DropPartialFrame()
    {
        if (HasPartialFrame)
        {
            Consume(1);
        }
    }

    private void Consume(int count)
    {
        _position += count;
        _start += count;
        _count -= count;
        if (_count == 0)
        {
            _start = 0;
        }
    }
}
