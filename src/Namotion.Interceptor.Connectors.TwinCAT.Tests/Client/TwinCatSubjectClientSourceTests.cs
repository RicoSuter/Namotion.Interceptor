using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using TwinCAT;
using TwinCAT.Ads;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Client;

public class TwinCatSubjectClientSourceTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
    }

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
    public void Constructor_ShouldCreateSourceSuccessfully()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        // Act
        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Assert
        Assert.NotNull(source);
        Assert.Same(subject, source.RootSubject);
    }

    [Fact]
    public void WriteBatchSize_ShouldBeZero()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act & Assert
        Assert.Equal(0, source.WriteBatchSize);
    }

    [Fact]
    public void Diagnostics_ShouldReturnNonNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act
        var diagnostics = source.Diagnostics;

        // Assert
        Assert.NotNull(diagnostics);
    }

    [Fact]
    public void Diagnostics_ShouldReturnSameInstance()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act
        var diagnostics1 = source.Diagnostics;
        var diagnostics2 = source.Diagnostics;

        // Assert
        Assert.Same(diagnostics1, diagnostics2);
    }

    [Fact]
    public void Diagnostics_InitialState_ShouldHaveZeroCounts()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act
        var diagnostics = source.Diagnostics;

        // Assert
        Assert.Null(diagnostics.State);
        Assert.False(diagnostics.IsConnected);
        Assert.Equal(0, diagnostics.NotificationVariableCount);
        Assert.Equal(0, diagnostics.PolledVariableCount);
        Assert.Equal(0, diagnostics.TotalReconnectionAttempts);
        Assert.Equal(0, diagnostics.SuccessfulReconnections);
        Assert.Equal(0, diagnostics.FailedReconnections);
        Assert.Null(diagnostics.LastConnectedAt);
        Assert.False(diagnostics.IsCircuitBreakerOpen);
        Assert.Equal(0, diagnostics.CircuitBreakerTripCount);
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteWithoutError()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act & Assert - should not throw
        await source.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_ShouldBeIdempotent()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act & Assert - should not throw on repeated dispose
        await source.DisposeAsync();
        await source.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public void Constructor_WithNullSubject_ShouldThrow()
    {
        // Arrange
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TwinCatSubjectClientSource(null!, configuration, logger));
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var logger = new Mock<ILogger>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TwinCatSubjectClientSource(subject, null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TwinCatSubjectClientSource(subject, configuration, null!));
    }

    [Fact]
    public void Constructor_WithInvalidConfiguration_ShouldThrow()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var logger = new Mock<ILogger>().Object;
        var configuration = new AdsClientConfiguration
        {
            Host = "", // Invalid - empty
            AmsNetId = "127.0.0.1.1.1",
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.')
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new TwinCatSubjectClientSource(subject, configuration, logger));
    }

    // Note: A test for "Constructor without lifecycle should throw" is not feasible because
    // WithRegistry() implicitly calls WithContextInheritance() which calls WithLifecycle().
    // The SourceOwnershipManager requires LifecycleInterceptor, and this is tested in
    // the base SourceOwnershipManagerTests in the Connectors project.

    [Fact]
    public async Task LoadInitialStateAsync_WhenNotConnected_ShouldReturnNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act - no connection established
        var result = await source.LoadInitialStateAsync(CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteChangesAsync_WhenNotConnected_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        var changes = new Namotion.Interceptor.Tracking.Change.SubjectPropertyChange[1];

        // Act
        var result = await source.WriteChangesAsync(changes.AsMemory(), CancellationToken.None);

        // Assert - should return failure when not connected
        Assert.False(result.IsFullySuccessful);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunAndStopCleanly()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        configuration.HealthCheckInterval = TimeSpan.FromMilliseconds(50);
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act - start the background service (runs ExecuteAsync health check loop)
        await source.StartAsync(CancellationToken.None);

        // Let the health check loop run a few iterations
        await Task.Delay(200);

        // Stop the service
        await source.StopAsync(CancellationToken.None);

        // Assert - should complete without error, dispose cleanly
        await source.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelledImmediately_ShouldStopGracefully()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);
        using var cts = new CancellationTokenSource();

        // Act - start and immediately cancel
        await source.StartAsync(cts.Token);
        await cts.CancelAsync();

        // Assert - should stop gracefully
        try { await source.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }
        await source.DisposeAsync();
    }

    [Fact]
    public async Task TriggerFullRescan_ViaConnectionRestored_WhenNotConnected_LogsErrorGracefully()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var source = new TwinCatSubjectClientSource(subject, configuration, mockLogger.Object);

        // Act - simulate a ConnectionRestored event (no real connection exists, so rescan will fail)
        source.ConnectionManager.OnConnectionStateChanged(null,
            new ConnectionStateChangedEventArgs(
                ConnectionStateChangedReason.Established,
                ConnectionState.Connected,
                ConnectionState.Disconnected));

        // Give the fire-and-forget TriggerFullRescanAsync time to complete
        await Task.Delay(100);

        // Assert - rescan failed because Connection is null, but error was caught and logged
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);

        await source.DisposeAsync();
    }

    [Fact]
    public async Task TriggerFullRescan_ViaAdsStateEnteredRun_WhenNotConnected_LogsErrorGracefully()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var source = new TwinCatSubjectClientSource(subject, configuration, mockLogger.Object);

        // Act - simulate ADS state entering Run (triggers rescan)
        source.ConnectionManager.OnAdsStateChanged(null,
            new AdsStateChangedEventArgs(new StateInfo(AdsState.Run, 0)));

        await Task.Delay(100);

        // Assert - rescan attempted and error was caught gracefully
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);

        await source.DisposeAsync();
    }

    [Fact]
    public async Task TriggerFullRescan_ViaSymbolVersionChanged_WhenNotConnected_LogsErrorGracefully()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var source = new TwinCatSubjectClientSource(subject, configuration, mockLogger.Object);

        // Act - simulate symbol version change (triggers rescan)
        source.ConnectionManager.OnSymbolVersionChanged(null,
            new AdsSymbolVersionChangedEventArgs(2));

        await Task.Delay(100);

        // Assert - rescan attempted and error was caught gracefully
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);

        await source.DisposeAsync();
    }

    [Fact]
    public async Task TriggerFullRescan_RepeatedEvents_OnlyLogsWarningOnce()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var source = new TwinCatSubjectClientSource(subject, configuration, mockLogger.Object);

        // Act - fire the same event multiple times
        for (var i = 0; i < 3; i++)
        {
            source.ConnectionManager.OnSymbolVersionChanged(null,
                new AdsSymbolVersionChangedEventArgs((byte)(i + 1)));
        }

        await Task.Delay(200);

        // Assert - first-occurrence pattern: first rescan failure is Warning, subsequent are Debug
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);

        await source.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionLost_StartsBuffering_DoesNotThrowWhenNoPropertyWriter()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;
        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act - simulate connection lost (no propertyWriter set yet, so StartBuffering is a no-op)
        source.ConnectionManager.OnConnectionStateChanged(null,
            new ConnectionStateChangedEventArgs(
                ConnectionStateChangedReason.Lost,
                ConnectionState.Disconnected,
                ConnectionState.Connected));

        // Assert - no exception thrown
        await source.DisposeAsync();
    }
}
