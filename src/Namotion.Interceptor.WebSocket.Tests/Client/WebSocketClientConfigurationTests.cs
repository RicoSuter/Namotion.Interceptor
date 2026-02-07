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

    [Fact]
    public void Validate_WithZeroConnectTimeout_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            ConnectTimeout = TimeSpan.Zero
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeReconnectDelay_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            ReconnectDelay = TimeSpan.FromSeconds(-1)
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithMaxReconnectDelayLessThanReconnectDelay_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            ReconnectDelay = TimeSpan.FromSeconds(10),
            MaxReconnectDelay = TimeSpan.FromSeconds(5)
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeWriteRetryQueueSize_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            WriteRetryQueueSize = -1
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithZeroReceiveTimeout_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            ReceiveTimeout = TimeSpan.Zero
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeBufferTime_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            BufferTime = TimeSpan.FromMilliseconds(-1)
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithZeroMaxMessageSize_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            MaxMessageSize = 0
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeWriteBatchSize_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            WriteBatchSize = -1
        };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void DefaultConfiguration_ShouldIncludeAllDefaults()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        Assert.Equal(TimeSpan.FromSeconds(60), configuration.MaxReconnectDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), configuration.ReceiveTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
        Assert.Equal(TimeSpan.FromSeconds(10), configuration.RetryTime);
        Assert.Equal(1000, configuration.WriteRetryQueueSize);
        Assert.Equal(10 * 1024 * 1024, configuration.MaxMessageSize);
        Assert.Equal(1000, configuration.WriteBatchSize);
        Assert.Null(configuration.PathProvider);
        Assert.Null(configuration.SubjectFactory);
        Assert.Null(configuration.Processors);
    }
}
