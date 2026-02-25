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
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var manager = new AdsConnectionManager(configuration, logger);

        Assert.NotNull(manager);
    }

    [Fact]
    public void Connection_Initially_ShouldBeNull()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Null(manager.Connection);
    }

    [Fact]
    public void SymbolLoader_Initially_ShouldBeNull()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Null(manager.SymbolLoader);
    }

    [Fact]
    public void CurrentAdsState_Initially_ShouldBeNull()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Null(manager.CurrentAdsState);
    }

    [Fact]
    public void IsConnected_Initially_ShouldBeFalse()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.False(manager.IsConnected);
    }

    [Fact]
    public void TotalReconnectionAttempts_Initially_Zero()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Equal(0, manager.TotalReconnectionAttempts);
    }

    [Fact]
    public void SuccessfulReconnections_Initially_Zero()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Equal(0, manager.SuccessfulReconnections);
    }

    [Fact]
    public void FailedReconnections_Initially_Zero()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Equal(0, manager.FailedReconnections);
    }

    [Fact]
    public void LastConnectedAt_Initially_Null()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Null(manager.LastConnectedAt);
    }

    [Fact]
    public void CircuitBreaker_Initially_Closed()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.False(manager.IsCircuitBreakerOpen);
        Assert.Equal(0, manager.CircuitBreakerTripCount);
    }

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var manager = new AdsConnectionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public void LogFirstOccurrence_FirstCall_LogsWarning()
    {
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = new AdsConnectionManager(CreateConfiguration(), mockLogger.Object);

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
        var manager = new AdsConnectionManager(CreateConfiguration(), mockLogger.Object);

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
    public void ClearFirstOccurrenceLog_ResetsToWarning()
    {
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var manager = new AdsConnectionManager(CreateConfiguration(), mockLogger.Object);

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
}
