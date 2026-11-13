using HomeBlaze.Abstractions;
using Xunit;

namespace HomeBlaze.OpcUa.Tests;

public class OpcUaStatusMessageTests
{
    [Fact]
    public void WhenRunningButNotOperational_ThenReturnsLastError()
    {
        // Arrange
        var lastError = new InvalidOperationException("Certificate rejected");

        // Act
        var message = OpcUaStatusMessage.Resolve(ServiceStatus.Running, isOperational: false, lastError, currentMessage: null);

        // Assert
        Assert.Equal("Certificate rejected", message);
    }

    [Fact]
    public void WhenRunningButNotOperationalWithoutError_ThenReturnsNull()
    {
        // Act
        var message = OpcUaStatusMessage.Resolve(ServiceStatus.Running, isOperational: false, lastError: null, currentMessage: "Old error");

        // Assert
        Assert.Null(message);
    }

    [Fact]
    public void WhenRunningAndOperational_ThenClearsMessage()
    {
        // Arrange
        var lastError = new InvalidOperationException("Certificate rejected");

        // Act
        var message = OpcUaStatusMessage.Resolve(ServiceStatus.Running, isOperational: true, lastError, currentMessage: "Certificate rejected");

        // Assert
        Assert.Null(message);
    }

    [Fact]
    public void WhenRunningBeforeFirstLivenessReport_ThenKeepsCurrentMessage()
    {
        // Act
        var message = OpcUaStatusMessage.Resolve(ServiceStatus.Running, isOperational: null, lastError: null, currentMessage: "Current");

        // Assert
        Assert.Equal("Current", message);
    }

    [Theory]
    [InlineData(ServiceStatus.Error)]
    [InlineData(ServiceStatus.Starting)]
    [InlineData(ServiceStatus.Stopping)]
    [InlineData(ServiceStatus.Stopped)]
    public void WhenNotRunning_ThenKeepsCurrentMessage(ServiceStatus status)
    {
        // Arrange
        var lastError = new InvalidOperationException("Certificate rejected");

        // Act
        var message = OpcUaStatusMessage.Resolve(status, isOperational: false, lastError, currentMessage: "Path is not configured");

        // Assert
        Assert.Equal("Path is not configured", message);
    }
}
