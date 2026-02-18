using System;
using Namotion.Interceptor.WebSocket.Client;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

public class WebSocketClientConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_ShouldHaveExpectedValues()
    {
        // Act
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.ReconnectDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.ConnectTimeout);
    }

    [Fact]
    public void Validate_WithoutServerUri_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithValidConfiguration_ShouldNotThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        // Act & Assert
        configuration.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_WithZeroConnectTimeout_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            ConnectTimeout = TimeSpan.Zero
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeReconnectDelay_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            ReconnectDelay = TimeSpan.FromSeconds(-1)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithMaxReconnectDelayLessThanReconnectDelay_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            ReconnectDelay = TimeSpan.FromSeconds(10),
            MaxReconnectDelay = TimeSpan.FromSeconds(5)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeWriteRetryQueueSize_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            WriteRetryQueueSize = -1
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithZeroReceiveTimeout_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            ReceiveTimeout = TimeSpan.Zero
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeBufferTime_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            BufferTime = TimeSpan.FromMilliseconds(-1)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithZeroMaxMessageSize_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            MaxMessageSize = 0
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativeWriteBatchSize_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws"),
            WriteBatchSize = -1
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void DefaultConfiguration_ShouldIncludeAllDefaults()
    {
        // Act
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(60), configuration.MaxReconnectDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), configuration.ReceiveTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
        Assert.Equal(TimeSpan.FromSeconds(10), configuration.RetryTime);
        Assert.Equal(1000, configuration.WriteRetryQueueSize);
        Assert.Equal(10 * 1024 * 1024, configuration.MaxMessageSize);
        Assert.Equal(1000, configuration.WriteBatchSize);
        Assert.Null(configuration.PathProvider);
        Assert.Null(configuration.SubjectFactory);
        Assert.Empty(configuration.Processors);
    }
}
