using System;
using Namotion.Interceptor.WebSocket.Client;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

public class WebSocketClientConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_ShouldHaveExpectedValues()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        Assert.Equal(TimeSpan.FromSeconds(5), configuration.ReconnectDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.ConnectTimeout);
    }

    [Fact]
    public void Validate_WithoutServerUri_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration();

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithValidConfiguration_ShouldNotThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        configuration.Validate(); // Should not throw
    }
}
