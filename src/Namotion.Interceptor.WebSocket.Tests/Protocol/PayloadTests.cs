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
        // Act
        var payload = new HelloPayload();

        // Assert
        Assert.Equal(1, payload.Version);
        Assert.Equal(WebSocketFormat.Json, payload.Format);
    }

    [Fact]
    public void WelcomePayload_ShouldHaveDefaultValues()
    {
        // Act
        var payload = new WelcomePayload();

        // Assert
        Assert.Equal(1, payload.Version);
        Assert.Equal(WebSocketFormat.Json, payload.Format);
        Assert.Null(payload.State);
    }

    [Fact]
    public void WelcomePayload_ShouldDefaultSequenceToZero()
    {
        // Act
        var payload = new WelcomePayload();

        // Assert
        Assert.Equal(0L, payload.Sequence);
    }

    [Fact]
    public void HeartbeatPayload_ShouldHaveDefaultValues()
    {
        // Act
        var payload = new HeartbeatPayload();

        // Assert
        Assert.Equal(0L, payload.Sequence);
    }

    [Fact]
    public void MessageType_ShouldIncludeHeartbeat()
    {
        // Act & Assert
        Assert.Equal(4, (int)MessageType.Heartbeat);
    }

    [Fact]
    public void ErrorPayload_ShouldSupportMultipleFailures()
    {
        // Act
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

        // Assert
        Assert.Equal(100, payload.Code);
        Assert.Equal(2, payload.Failures!.Count);
        Assert.Equal("Motor/Speed", payload.Failures[0].Path);
    }

    [Fact]
    public void UpdatePayload_ShouldInheritFromSubjectUpdate()
    {
        // Act
        var payload = new UpdatePayload
        {
            Sequence = 42,
            Root = "1",
            Subjects = { ["1"] = new Dictionary<string, SubjectPropertyUpdate>() }
        };

        // Assert
        Assert.Equal(42, payload.Sequence);
        Assert.Equal("1", payload.Root);
        Assert.IsAssignableFrom<SubjectUpdate>(payload);
    }

    [Fact]
    public void UpdatePayload_SequenceShouldDefaultToNull()
    {
        // Act
        var payload = new UpdatePayload();

        // Assert
        Assert.Null(payload.Sequence);
    }
}
