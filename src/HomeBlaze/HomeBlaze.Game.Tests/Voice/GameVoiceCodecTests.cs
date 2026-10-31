using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests.Voice;

public class GameVoiceCodecTests
{
    [Fact]
    public void WhenAFrameIsDecoded_ThenItsSamplesAreReadAsLittleEndian()
    {
        // Arrange
        var frame = new byte[GameVoiceSettings.FrameBytes];
        frame[0] = 0x34;
        frame[1] = 0x12;
        frame[2] = 0xFF;
        frame[3] = 0xFF;
        var samples = new short[GameVoiceSettings.SamplesPerFrame];

        // Act
        GameVoiceCodec.Decode(frame, samples);

        // Assert
        Assert.Equal((short)0x1234, samples[0]);
        Assert.Equal((short)-1, samples[1]);
        Assert.Equal((short)0, samples[2]);
    }

    [Fact]
    public void WhenAFrameHasTheWrongSize_ThenDecodingThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => GameVoiceCodec.Decode(new byte[10], new short[GameVoiceSettings.SamplesPerFrame]));
    }

    [Fact]
    public void WhenSamplesAreEncoded_ThenTheyAreClampedAndWrittenAsLittleEndian()
    {
        // Arrange
        var samples = new int[GameVoiceSettings.SamplesPerFrame];
        samples[0] = 0x1234;
        samples[1] = 40000;
        samples[2] = -40000;

        // Act
        var frame = GameVoiceCodec.Encode(samples);

        // Assert
        Assert.Equal(GameVoiceSettings.FrameBytes, frame.Length);
        Assert.Equal(new byte[] { 0x34, 0x12 }, frame[..2]);
        Assert.Equal(short.MaxValue, VoiceFrames.ReadSample(frame, 1));
        Assert.Equal(short.MinValue, VoiceFrames.ReadSample(frame, 2));
    }
}
