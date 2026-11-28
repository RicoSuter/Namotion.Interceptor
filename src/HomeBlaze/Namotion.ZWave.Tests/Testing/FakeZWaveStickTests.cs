using Namotion.ZWave.SerialApi;

namespace Namotion.ZWave.Tests.Testing;

public class FakeZWaveStickTests
{
    [Fact]
    public async Task WhenHostWritesARequest_ThenTheStickAcknowledgesAndAnswersIt()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        stick.SetResponse(FunctionId.GetControllerId, 0xC0, 0xFF, 0xEE, 0x01, 0x01);
        await using var stream = await stick.OpenAsync(CancellationToken.None);

        // Act
        await stream.WriteAsync(FrameEncoder.Encode(FrameType.Request, FunctionId.GetControllerId, []));
        var parser = new FrameParser();
        var frames = new List<ParsedFrame>();
        var buffer = new byte[64];
        while (frames.Count < 2)
        {
            var count = await stream.ReadAsync(buffer);
            parser.Append(buffer.AsSpan(0, count));
            while (parser.TryRead(out var frame))
            {
                frames.Add(frame);
            }
        }

        // Assert
        Assert.Equal(FrameKind.Ack, frames[0].Kind);
        Assert.Equal(FrameType.Response, frames[1].Data.Type);
        Assert.Equal(FunctionId.GetControllerId, frames[1].Data.Function);
        Assert.Single(stick.ReceivedDataFrames);
    }
}
