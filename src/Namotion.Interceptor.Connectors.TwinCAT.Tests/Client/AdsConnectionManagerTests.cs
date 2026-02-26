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

    #region Constructor

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        var logger = new Mock<ILogger>().Object;

        Assert.Throws<ArgumentNullException>(() =>
            new AdsConnectionManager(null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        var configuration = CreateConfiguration();

        Assert.Throws<ArgumentNullException>(() =>
            new AdsConnectionManager(configuration, null!));
    }

    [Fact]
    public void Constructor_WithValidParameters_ShouldCreate()
    {
        var manager = CreateManager();

        Assert.NotNull(manager);
    }

    #endregion

    #region Initial State

    [Fact]
    public void Connection_Initially_ShouldBeNull()
    {
        var manager = CreateManager();

        Assert.Null(manager.Connection);
    }

    [Fact]
    public void SymbolLoader_Initially_ShouldBeNull()
    {
        var manager = CreateManager();

        Assert.Null(manager.SymbolLoader);
    }

    [Fact]
    public void CurrentAdsState_Initially_ShouldBeNull()
    {
        var manager = CreateManager();

        Assert.Null(manager.CurrentAdsState);
    }

    [Fact]
    public void IsConnected_Initially_ShouldBeFalse()
    {
        var manager = CreateManager();

        Assert.False(manager.IsConnected);
    }

    [Fact]
    public void TotalReconnectionAttempts_Initially_Zero()
    {
        var manager = CreateManager();

        Assert.Equal(0, manager.TotalReconnectionAttempts);
    }

    [Fact]
    public void SuccessfulReconnections_Initially_Zero()
    {
        var manager = CreateManager();

        Assert.Equal(0, manager.SuccessfulReconnections);
    }

    [Fact]
    public void FailedReconnections_Initially_Zero()
    {
        var manager = CreateManager();

        Assert.Equal(0, manager.FailedReconnections);
    }

    [Fact]
    public void LastConnectedAt_Initially_Null()
    {
        var manager = CreateManager();

        Assert.Null(manager.LastConnectedAt);
    }

    [Fact]
    public void CircuitBreaker_Initially_Closed()
    {
        var manager = CreateManager();

        Assert.False(manager.IsCircuitBreakerOpen);
        Assert.Equal(0, manager.CircuitBreakerTripCount);
    }

    #endregion

    #region RecreateSymbolLoader

    [Fact]
    public void RecreateSymbolLoader_WhenNoSession_ShouldNotThrow()
    {
        var manager = CreateManager();

        // No session established, should be a no-op
        manager.RecreateSymbolLoader();

        Assert.Null(manager.SymbolLoader);
    }

    [Fact]
    public void RecreateSymbolLoader_WhenNoSession_ShouldKeepSymbolLoaderNull()
    {
        var manager = CreateManager();

        manager.RecreateSymbolLoader();
        manager.RecreateSymbolLoader();

        Assert.Null(manager.SymbolLoader);
    }

    #endregion

    #region Dispose

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        var manager = CreateManager();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var manager = CreateManager();

        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_PropertiesStillAccessible()
    {
        var manager = CreateManager();
        await manager.DisposeAsync();

        // Properties should still be readable after dispose without throwing
        Assert.Null(manager.Connection);
        Assert.Null(manager.SymbolLoader);
        Assert.Null(manager.CurrentAdsState);
        Assert.False(manager.IsConnected);
    }

    #endregion

    #region First-Occurrence Logging (without exception)

    [Fact]
    public void LogFirstOccurrence_FirstCall_LogsWarning()
    {
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        manager.LogFirstOccurrence("TestCategory", null, "Test message");

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
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

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
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

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
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        manager.LogFirstOccurrence("TestCategory", null, "Test message");
        manager.ClearFirstOccurrenceLog("TestCategory");
        manager.LogFirstOccurrence("TestCategory", null, "Test message");

        // Should have 2 warnings (first call + after clear) and 0 debug
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
        var manager = CreateManager();

        // Should not throw when clearing a category that was never logged
        manager.ClearFirstOccurrenceLog("NonExistentCategory");
    }

    #endregion

    #region First-Occurrence Logging (with exception)

    [Fact]
    public void LogFirstOccurrence_WithException_FirstCall_LogsWarning()
    {
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception = new InvalidOperationException("Test error");

        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");

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
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception1 = new InvalidOperationException("First error");
        var exception2 = new InvalidOperationException("Second error");

        manager.LogFirstOccurrence("TestCategory", exception1, "Error occurred");
        manager.LogFirstOccurrence("TestCategory", exception2, "Error occurred");

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
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);
        var exception = new InvalidOperationException("Test error");

        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");
        manager.ClearFirstOccurrenceLog("TestCategory");
        manager.LogFirstOccurrence("TestCategory", exception, "Error occurred");

        // Two warnings, zero debug
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    #endregion

    #region Multiple Categories

    [Fact]
    public void LogFirstOccurrence_DifferentCategories_TrackedIndependently()
    {
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");
        manager.LogFirstOccurrence("HealthCheck", null, "Health check error");

        // Each category gets its own first-occurrence warning
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
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        // First call for each category: warning
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");

        // Second call for each category: debug
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");

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
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = CreateManager(mockLogger);

        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");

        // Clear only "Connection"
        manager.ClearFirstOccurrenceLog("Connection");

        // "Connection" resets to warning, "BatchPoll" stays as debug
        manager.LogFirstOccurrence("Connection", null, "Connection error");
        manager.LogFirstOccurrence("BatchPoll", null, "Polling error");

        // 3 warnings total: Connection(1st), BatchPoll(1st), Connection(after clear)
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

    #endregion

    #region ConnectWithRetryAsync

    [Fact]
    public async Task ConnectWithRetryAsync_WhenCancelledImmediately_ShouldReturn()
    {
        var manager = CreateManager();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Should not throw, just return when already cancelled
        await manager.ConnectWithRetryAsync(cancellationTokenSource.Token);

        Assert.Equal(0, manager.TotalReconnectionAttempts);
    }

    #endregion
}
