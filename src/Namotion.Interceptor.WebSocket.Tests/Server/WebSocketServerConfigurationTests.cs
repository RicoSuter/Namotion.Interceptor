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
    public void DefaultConfiguration_ShouldIncludeAllDefaults()
    {
        var configuration = new WebSocketServerConfiguration();

        Assert.Equal(1000, configuration.MaxConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), configuration.HelloTimeout);
        Assert.Equal(10 * 1024 * 1024, configuration.MaxMessageSize);
        Assert.Equal(1000, configuration.WriteBatchSize);
        Assert.Null(configuration.BindAddress);
        Assert.Null(configuration.PathProvider);
        Assert.Null(configuration.SubjectFactory);
        Assert.Null(configuration.Processors);
    }

    [Fact]
    public void Validate_WithInvalidPort_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { Port = 0 };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithPortAboveRange_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { Port = 70000 };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithEmptyPath_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { Path = "" };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithWhitespacePath_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { Path = "   " };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeBufferTime_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration
        {
            BufferTime = TimeSpan.FromMilliseconds(-1)
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithZeroMaxMessageSize_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { MaxMessageSize = 0 };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithZeroMaxConnections_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { MaxConnections = 0 };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithZeroHelloTimeout_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration
        {
            HelloTimeout = TimeSpan.Zero
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeWriteBatchSize_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { WriteBatchSize = -1 };

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
