using System;
using Namotion.Interceptor.WebSocket.Server;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class WebSocketServerConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_ShouldHaveExpectedValues()
    {
        var configuration = new WebSocketServerConfiguration();

        Assert.Equal(8080, configuration.Port);
        Assert.Equal("/ws", configuration.Path);
        Assert.Null(configuration.BindAddress);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
    }

    [Fact]
    public void Validate_WithInvalidPort_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { Port = 0 };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithValidConfiguration_ShouldNotThrow()
    {
        var configuration = new WebSocketServerConfiguration
        {
            Port = 8080,
            Path = "/ws"
        };

        configuration.Validate(); // Should not throw
    }
}
