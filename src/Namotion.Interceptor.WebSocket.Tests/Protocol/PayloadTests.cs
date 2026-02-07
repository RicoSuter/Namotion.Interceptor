using System.Collections.Generic;
using Namotion.Interceptor.Connectors.Updates;
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
        Assert.Equal(WebSocketFormat.Json, payload.Format);
    }

    [Fact]
    public void WelcomePayload_ShouldHaveDefaultValues()
    {
        var payload = new WelcomePayload();

        Assert.Equal(1, payload.Version);
        Assert.Equal(WebSocketFormat.Json, payload.Format);
        Assert.Null(payload.State);
    }

    [Fact]
    public void WelcomePayload_ShouldDefaultSequenceToZero()
    {
        var payload = new WelcomePayload();

        Assert.Equal(0L, payload.Sequence);
    }

    [Fact]
    public void HeartbeatPayload_ShouldHaveDefaultValues()
    {
        var payload = new HeartbeatPayload();

        Assert.Equal(0L, payload.Sequence);
    }

    [Fact]
    public void MessageType_ShouldIncludeHeartbeat()
    {
        Assert.Equal(4, (int)MessageType.Heartbeat);
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

    [Fact]
    public void UpdatePayload_ShouldInheritFromSubjectUpdate()
    {
        var payload = new UpdatePayload
        {
            Sequence = 42,
            Root = "1",
            Subjects = { ["1"] = new Dictionary<string, SubjectPropertyUpdate>() }
        };

        Assert.Equal(42, payload.Sequence);
        Assert.Equal("1", payload.Root);
        Assert.IsAssignableFrom<SubjectUpdate>(payload);
    }

    [Fact]
    public void UpdatePayload_SequenceShouldDefaultToNull()
    {
        var payload = new UpdatePayload();
        Assert.Null(payload.Sequence);
    }
}
