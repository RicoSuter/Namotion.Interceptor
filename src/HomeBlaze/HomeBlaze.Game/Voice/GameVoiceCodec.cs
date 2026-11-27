using System.Buffers.Binary;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// Turns frames into samples and back: 16-bit little-endian PCM, the one place in C# to change for another codec.
/// </summary>
internal static class GameVoiceCodec
{
    /// <summary>
    /// Decodes a frame into its samples.
    /// </summary>
    /// <exception cref="ArgumentException">The frame does not have exactly <see cref="GameVoiceSettings.FrameBytes"/>
    /// bytes, or the samples have room for less than a frame.</exception>
    public static void Decode(ReadOnlySpan<byte> frame, Span<short> samples)
    {
        if (frame.Length != GameVoiceSettings.FrameBytes)
        {
            throw new ArgumentException($"A frame has exactly {GameVoiceSettings.FrameBytes} bytes.", nameof(frame));
        }

        if (samples.Length < GameVoiceSettings.SamplesPerFrame)
        {
            throw new ArgumentException($"The samples need room for {GameVoiceSettings.SamplesPerFrame} samples.", nameof(samples));
        }

        for (var index = 0; index < GameVoiceSettings.SamplesPerFrame; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(frame[(index * 2)..]);
        }
    }

    /// <summary>
    /// Encodes summed samples into a new frame, clamping each to the 16-bit range.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<int> samples)
    {
        var frame = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = (short)Math.Clamp(samples[index], short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(index * 2), sample);
        }

        return frame;
    }
}
