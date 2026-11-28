using Namotion.ZWave.SerialApi;

namespace Namotion.ZWave.Tests.SerialApi;

public class FrameParserTests
{
    [Theory]
    [InlineData(FrameHeader.Ack, (int)FrameKind.Ack)]
    [InlineData(FrameHeader.Nak, (int)FrameKind.Nak)]
    [InlineData(FrameHeader.Can, (int)FrameKind.Can)]
    public void WhenSingleByteFrameArrives_ThenItIsReadAsItsKind(byte header, int expected)
    {
        // Arrange
        var parser = new FrameParser();
        parser.Append([header]);

        // Act
        var read = parser.TryRead(out var frame);

        // Assert
        Assert.True(read);
        Assert.Equal((FrameKind)expected, frame.Kind);
        Assert.False(parser.TryRead(out _));
    }

    [Fact]
    public void WhenDataFrameArrives_ThenTypeFunctionAndPayloadAreRead()
    {
        // Arrange
        var parser = new FrameParser();
        parser.Append(FrameEncoder.Encode(FrameType.Response, 0x20, [0xC0, 0xFF, 0xEE, 0x01, 0x01]));

        // Act
        var read = parser.TryRead(out var frame);

        // Assert
        Assert.True(read);
        Assert.Equal(FrameKind.Data, frame.Kind);
        Assert.Equal(FrameType.Response, frame.Data.Type);
        Assert.Equal(0x20, frame.Data.Function);
        Assert.Equal(new byte[] { 0xC0, 0xFF, 0xEE, 0x01, 0x01 }, frame.Data.Payload);
    }

    [Fact]
    public void WhenDataFrameArrivesByteByByte_ThenItIsReadOnlyAfterTheLastByte()
    {
        // Arrange
        var parser = new FrameParser();
        var bytes = FrameEncoder.Encode(FrameType.Request, 0x04, [0x00, 0x05, 0x03, 0x20, 0x03, 0xFF]);

        // Act
        var readsBeforeLastByte = 0;
        for (var index = 0; index < bytes.Length - 1; index++)
        {
            parser.Append(bytes.AsSpan(index, 1));
            if (parser.TryRead(out _))
            {
                readsBeforeLastByte++;
            }
        }

        parser.Append(bytes.AsSpan(bytes.Length - 1, 1));
        var read = parser.TryRead(out var frame);

        // Assert
        Assert.Equal(0, readsBeforeLastByte);
        Assert.True(read);
        Assert.Equal(0x04, frame.Data.Function);
    }

    [Fact]
    public void WhenGarbagePrecedesAFrame_ThenItIsSkipped()
    {
        // Arrange
        var parser = new FrameParser();
        parser.Append([0x00, 0x42, 0xFF]);
        parser.Append(FrameEncoder.Encode(FrameType.Response, 0x15, [0x01]));

        // Act
        var read = parser.TryRead(out var frame);

        // Assert
        Assert.True(read);
        Assert.Equal(FrameKind.Data, frame.Kind);
        Assert.Equal(0x15, frame.Data.Function);
    }

    [Fact]
    public void WhenChecksumIsWrong_ThenAChecksumErrorIsReadAndTheNextFrameStillParses()
    {
        // Arrange
        var parser = new FrameParser();
        parser.Append([0x01, 0x03, 0x00, 0x15, 0x00]);
        parser.Append([FrameHeader.Ack]);

        // Act
        var firstRead = parser.TryRead(out var first);
        var secondRead = parser.TryRead(out var second);

        // Assert
        Assert.True(firstRead);
        Assert.Equal(FrameKind.ChecksumError, first.Kind);
        Assert.True(secondRead);
        Assert.Equal(FrameKind.Ack, second.Kind);
    }

    [Fact]
    public void WhenLengthIsTooShort_ThenTheStartOfFrameIsSkipped()
    {
        // Arrange
        var parser = new FrameParser();
        parser.Append([0x01, 0x02]);
        parser.Append([FrameHeader.Ack]);

        // Act
        var read = parser.TryRead(out var frame);

        // Assert
        Assert.True(read);
        Assert.Equal(FrameKind.Ack, frame.Kind);
    }

    [Fact]
    public void WhenTwoFramesArriveInOneChunk_ThenBothAreRead()
    {
        // Arrange
        var parser = new FrameParser();
        var bytes = FrameEncoder.Encode(FrameType.Response, 0x15, [0x01])
            .Concat(FrameEncoder.Encode(FrameType.Response, 0x20, [0x02]))
            .ToArray();
        parser.Append(bytes);

        // Act
        parser.TryRead(out var first);
        parser.TryRead(out var second);

        // Assert
        Assert.Equal(0x15, first.Data.Function);
        Assert.Equal(0x20, second.Data.Function);
        Assert.False(parser.TryRead(out _));
    }

    [Fact]
    public void WhenManyChunksArrive_ThenTheBufferIsReusedAndFramesStillParse()
    {
        // Arrange
        var parser = new FrameParser();
        var bytes = FrameEncoder.Encode(FrameType.Response, 0x07, new byte[40]);

        // Act & Assert
        for (var iteration = 0; iteration < 100; iteration++)
        {
            parser.Append(bytes);
            Assert.True(parser.TryRead(out var frame));
            Assert.Equal(40, frame.Data.Payload.Length);
        }
    }
}
