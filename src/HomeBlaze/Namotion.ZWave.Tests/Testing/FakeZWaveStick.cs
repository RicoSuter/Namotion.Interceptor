using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Transport;

namespace Namotion.ZWave.Tests.Testing;

/// <summary>
/// A scripted Z-Wave controller for tests.
/// </summary>
internal sealed class FakeZWaveStick : IZWaveTransport
{
    private readonly Lock _lock = new();
    private readonly FrameParser _parser = new();
    private readonly List<ParsedFrame> _receivedFrames = [];
    private readonly Dictionary<byte, byte[]> _responses = [];
    private readonly Queue<byte> _acknowledgements = new();
    private FakeSerialStream? _stream;

    /// <summary>
    /// Whether request frames are acknowledged when no other acknowledgement is queued.
    /// </summary>
    public bool AcknowledgesFrames { get; set; } = true;

    public bool IsDisposed => _stream?.IsDisposed == true;

    public IReadOnlyList<ParsedFrame> ReceivedFrames
    {
        get
        {
            lock (_lock)
            {
                return _receivedFrames.ToArray();
            }
        }
    }

    public IReadOnlyList<DataFrame> ReceivedDataFrames =>
        ReceivedFrames.Where(frame => frame.Kind == FrameKind.Data).Select(frame => frame.Data).ToArray();

    public int ReceivedAckCount => ReceivedFrames.Count(frame => frame.Kind == FrameKind.Ack);

    public int ReceivedNakCount => ReceivedFrames.Count(frame => frame.Kind == FrameKind.Nak);

    /// <summary>
    /// Creates a stick that answers the startup requests like a 500-series static controller
    /// with home ID 0xC0FFEE01, own node ID 1 and the nodes 1, 2 and 5.
    /// </summary>
    public static FakeZWaveStick CreateWithController()
    {
        var stick = new FakeZWaveStick();
        stick.SetResponse(FunctionId.GetControllerVersion, [.. "Z-Wave 4.05"u8, 0x00, 0x01]);
        stick.SetResponse(FunctionId.GetControllerId, 0xC0, 0xFF, 0xEE, 0x01, 0x01);

        var functionBitmask = new byte[32];
        foreach (var function in new[] { FunctionId.GetSerialApiInitData, FunctionId.GetControllerCapabilities, FunctionId.GetSerialApiCapabilities, FunctionId.GetControllerVersion, FunctionId.GetControllerId })
        {
            functionBitmask[(function - 1) / 8] |= (byte)(1 << ((function - 1) % 8));
        }

        stick.SetResponse(FunctionId.GetSerialApiCapabilities, [0x01, 0x02, 0x00, 0x86, 0x00, 0x01, 0x00, 0x5A, .. functionBitmask]);

        var nodeBitmask = new byte[29];
        nodeBitmask[0] = 0b0001_0011;
        stick.SetResponse(FunctionId.GetSerialApiInitData, [0x05, 0x08, 29, .. nodeBitmask, 0x05, 0x00]);
        stick.SetResponse(FunctionId.GetControllerCapabilities, 0x1C);
        return stick;
    }

    public void SetResponse(byte function, params byte[] payload)
    {
        lock (_lock)
        {
            _responses[function] = payload;
        }
    }

    /// <summary>
    /// Queues a header (ACK, NAK or CAN) that answers the next request frame instead of the default ACK.
    /// A request answered with NAK or CAN gets no response frame.
    /// </summary>
    public void EnqueueAcknowledgement(byte header)
    {
        lock (_lock)
        {
            _acknowledgements.Enqueue(header);
        }
    }

    public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        _stream = new FakeSerialStream(this);
        return ValueTask.FromResult<Stream>(_stream);
    }

    public void SendToHost(params byte[] bytes) => GetStream().Push(bytes);

    public void SendFrame(FrameType type, byte function, params byte[] payload) =>
        SendToHost(FrameEncoder.Encode(type, function, payload));

    /// <summary>
    /// Ends the stream, as an unplugged stick does.
    /// </summary>
    public void Disconnect() => GetStream().EndOfStream();

    internal void OnHostWrite(ReadOnlySpan<byte> bytes)
    {
        var replies = new List<byte[]>();
        lock (_lock)
        {
            _parser.Append(bytes);
            while (_parser.TryRead(out var frame))
            {
                _receivedFrames.Add(frame);
                if (frame.Kind != FrameKind.Data || frame.Data.Type != FrameType.Request)
                {
                    continue;
                }

                if (_acknowledgements.TryDequeue(out var header))
                {
                    replies.Add([header]);
                    if (header != FrameHeader.Ack)
                    {
                        continue;
                    }
                }
                else if (AcknowledgesFrames)
                {
                    replies.Add([FrameHeader.Ack]);
                }
                else
                {
                    continue;
                }

                if (_responses.TryGetValue(frame.Data.Function, out var payload))
                {
                    replies.Add(FrameEncoder.Encode(FrameType.Response, frame.Data.Function, payload));
                }
            }
        }

        foreach (var reply in replies)
        {
            GetStream().Push(reply);
        }
    }

    private FakeSerialStream GetStream() =>
        _stream ?? throw new InvalidOperationException("The fake stick has not been opened.");
}
