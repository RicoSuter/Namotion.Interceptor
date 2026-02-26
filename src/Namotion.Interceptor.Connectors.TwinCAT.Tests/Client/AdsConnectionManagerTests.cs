using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Client;

public class AdsConnectionManagerTests
{
    private static AdsClientConfiguration CreateConfiguration()
    {
        return new AdsClientConfiguration
        {
            Host = "127.0.0.1",
            AmsNetId = "127.0.0.1.1.1",
            AmsPort = 851,
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.')
        };
    }

    private static AdsConnectionManager CreateManager(Mock<ILogger>? mockLogger = null)
    {
        return new AdsConnectionManager(
            CreateConfiguration(),
            (mockLogger ?? new Mock<ILogger>()).Object);
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        // Arrange
        var logger = new Mock<ILogger>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AdsConnectionManager(null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        // Arrange
        var configuration = CreateConfiguration();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AdsConnectionManager(configuration, null!));
    }

    [Fact]
    public void Constructor_WithValidParameters_ShouldCreate()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.NotNull(manager);
    }

    [Fact]
    public void Connection_Initially_ShouldBeNull()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Null(manager.Connection);
    }

    [Fact]
    public void SymbolLoader_Initially_ShouldBeNull()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Null(manager.SymbolLoader);
    }

    [Fact]
    public void CurrentAdsState_Initially_ShouldBeNull()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Null(manager.CurrentAdsState);
    }

    [Fact]
    public void IsConnected_Initially_ShouldBeFalse()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.False(manager.IsConnected);
    }

    [Fact]
    public void TotalReconnectionAttempts_Initially_Zero()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Equal(0, manager.TotalReconnectionAttempts);
    }

    [Fact]
    public void SuccessfulReconnections_Initially_Zero()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Equal(0, manager.SuccessfulReconnections);
    }

    [Fact]
    public void FailedReconnections_Initially_Zero()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Equal(0, manager.FailedReconnections);
    }

    [Fact]
    public void LastConnectedAt_Initially_Null()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Null(manager.LastConnectedAt);
    }

    [Fact]
    public void CircuitBreaker_Initially_Closed()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.False(manager.IsCircuitBreakerOpen);
        Assert.Equal(0, manager.CircuitBreakerTripCount);
    }

    [Fact]
    public void RecreateSymbolLoader_WhenNoSession_ShouldNotThrow()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.RecreateSymbolLoader();

        // Assert
        Assert.Null(manager.SymbolLoader);
    }

    [Fact]
    public void RecreateSymbolLoader_WhenNoSession_ShouldKeepSymbolLoaderNull()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.RecreateSymbolLoader();
        manager.RecreateSymbolLoader();

        // Assert
        Assert.Null(manager.SymbolLoader);
    }

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_PropertiesStillAccessible()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        await manager.DisposeAsync();

        // Assert
        Assert.Null(manager.Connection);
        Assert.Null(manager.SymbolLoader);
        Assert.Null(manager.CurrentAdsState);
        Assert.False(manager.IsConnected);
    }

    [Fact]
    public void LogFirstOccurrence_FirstCall_LogsWarning()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFirstOccurrence_SecondCall_LogsDebug()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFirstOccurrence_ThirdCall_StillLogsDebug()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void ClearFirstOccurrenceLog_ResetsToWarning()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.ClearFirstOccurrenceLog("TestCategory");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Assert - should have 2 warnings (first call + after clear) and 0 debug
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void ClearFirstOccurrenceLog_NonExistentCategory_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        manager.ClearFirstOccurrenceLog("NonExistentCategory");
    }

    [Fact]
    public void LogFirstOccurrence_WithException_FirstCall_LogsWarning()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception = new InvalidOperationException("Test error");

        // Act
        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFirstOccurrence_WithException_SecondCall_LogsDebug()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception1 = new InvalidOperationException("First error");
        var exception2 = new InvalidOperationException("Second error");

        // Act
        manager.LogFirstOccurrence("TestCategory", exception1, "Error occurred");
        manager.LogFirstOccurrence("TestCategory", exception2, "Error occurred");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception1,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception2,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFirstOccurrence_WithException_AfterClear_LogsWarningAgain()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception = new InvalidOperationException("Test error");

        // Act
        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");
        manager.ClearFirstOccurrenceLog("TestCategory");
        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");

        // Assert - two warnings, zero debug
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void LogFirstOccurrence_DifferentCategories_TrackedIndependently()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");
        manager.LogFirstOccurrence("HealthCheck", null, "Health check error");

        // Assert - each category gets its own first-occurrence warning
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));
    }

    [Fact]
    public void LogFirstOccurrence_DifferentCategories_SecondCallPerCategory_LogsDebug()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");

        // Assert
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public void ClearFirstOccurrenceLog_OnlyAffectsSpecifiedCategory()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // Act
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");
        manager.ClearFirstOccurrenceLog("Connection");
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");

        // Assert - 3 warnings: Connection(1st), BatchPoll(1st), Connection(after clear)
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));

        // 1 debug: BatchPoll(2nd)
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ConnectWithRetryAsync_WhenCancelledImmediately_ShouldReturn()
    {
        // Arrange
        var manager = CreateManager();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act
        await manager.ConnectWithRetryAsync(cancellationTokenSource.Token);

        // Assert
        Assert.Equal(0, manager.TotalReconnectionAttempts);
    }
}
