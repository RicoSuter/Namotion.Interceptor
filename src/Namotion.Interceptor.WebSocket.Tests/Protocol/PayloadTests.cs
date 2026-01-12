using Namotion.Interceptor.WebSocket.Protocol;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Protocol;

public class PayloadTests
{
    [Fact]
    public void HelloPayload_ShouldHaveDefaultValues()
    {
        var payload = new HelloPayload();

        Assert.Equal(1, payload.Version);
        Assert.Equal(WsFormat.Json, payload.Format);
    }

    [Fact]
    public void WelcomePayload_ShouldHaveDefaultValues()
    {
        var payload = new WelcomePayload();

        Assert.Equal(1, payload.Version);
        Assert.Equal(WsFormat.Json, payload.Format);
        Assert.Null(payload.State);
    }

    [Fact]
    public void ErrorPayload_ShouldSupportMultipleFailures()
    {
        var payload = new ErrorPayload
        {
            Code = 100,
            Message = "Multiple failures",
            Failures =
            [
                new PropertyFailure { Path = "Motor/Speed", Code = 101, Message = "Read-only" },
                new PropertyFailure { Path = "Sensor/Unknown", Code = 100, Message = "Not found" }
            ]
        };

        Assert.Equal(100, payload.Code);
        Assert.Equal(2, payload.Failures!.Count);
        Assert.Equal("Motor/Speed", payload.Failures[0].Path);
    }
}
