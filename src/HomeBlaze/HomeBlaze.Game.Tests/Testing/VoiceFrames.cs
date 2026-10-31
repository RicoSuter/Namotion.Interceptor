using System.Buffers.Binary;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Builds and reads voice frames of 16-bit little-endian samples.
/// </summary>
internal static class VoiceFrames
{
    /// <summary>
    /// Creates a frame whose samples all have the value.
    /// </summary>
    public static byte[] Create(short sample)
    {
        var frame = new byte[GameVoiceSettings.FrameBytes];
        for (var index = 0; index < GameVoiceSettings.SamplesPerFrame; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(index * 2), sample);
        }

        return frame;
    }

    public static short ReadSample(byte[] frame, int index) =>
        BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(index * 2));
}
