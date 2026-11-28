using Namotion.ZWave.SerialApi;

namespace Namotion.ZWave.Tests.SerialApi;

public class FrameEncoderTests
{
    [Theory]
    [InlineData(FunctionId.GetControllerVersion, new byte[] { 0x01, 0x03, 0x00, 0x15, 0xE9 })]
    [InlineData(FunctionId.GetControllerId, new byte[] { 0x01, 0x03, 0x00, 0x20, 0xDC })]
    public void WhenRequestWithoutPayloadIsEncoded_ThenItMatchesTheKnownFrame(byte function, byte[] expected)
    {
        // Act
        var frame = FrameEncoder.Encode(FrameType.Request, function, []);

        // Assert
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void WhenFrameWithPayloadIsEncoded_ThenLengthAndChecksumCoverThePayload()
    {
        // Act
        var frame = FrameEncoder.Encode(FrameType.Response, 0x07, [0xAA, 0x55]);

        // Assert
        Assert.Equal(new byte[] { 0x01, 0x05, 0x01, 0x07, 0xAA, 0x55, (byte)(0xFF ^ 0x05 ^ 0x01 ^ 0x07 ^ 0xAA ^ 0x55) }, frame);
    }

    [Fact]
    public void WhenPayloadIsTooLong_ThenEncodeThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameEncoder.Encode(FrameType.Request, 0x13, new byte[253]));
    }
}
