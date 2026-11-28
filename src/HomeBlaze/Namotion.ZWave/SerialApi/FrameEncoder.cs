namespace Namotion.ZWave.SerialApi;

internal static class FrameEncoder
{
    private const int MaximumPayloadLength = byte.MaxValue - 3;

    public static byte[] Encode(FrameType type, byte function, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"A Serial API payload holds at most {MaximumPayloadLength} bytes.");
        }

        var frame = new byte[payload.Length + 5];
        frame[0] = FrameHeader.StartOfFrame;
        frame[1] = (byte)(payload.Length + 3);
        frame[2] = (byte)type;
        frame[3] = function;
        payload.CopyTo(frame.AsSpan(4));
        frame[^1] = ComputeChecksum(frame.AsSpan(1, frame.Length - 2));
        return frame;
    }

    /// <summary>
    /// Computes the checksum over the bytes from the length byte to the last payload byte.
    /// </summary>
    public static byte ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        byte checksum = 0xFF;
        foreach (var value in bytes)
        {
            checksum ^= value;
        }

        return checksum;
    }
}
