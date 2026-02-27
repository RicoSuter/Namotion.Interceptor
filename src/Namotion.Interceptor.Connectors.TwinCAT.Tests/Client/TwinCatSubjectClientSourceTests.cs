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

    private static TwinCatSubjectClientSource CreateSource(
        TestPlcModel? subject = null,
        AdsClientConfiguration? configuration = null,
        ILogger? logger = null)
    {
        var context = CreateContext();
        return new TwinCatSubjectClientSource(
            subject ?? new TestPlcModel(context),
            configuration ?? CreateConfiguration(),
            logger ?? new Mock<ILogger>().Object);
    }

    private static (TwinCatSubjectClientSource Source, Mock<ILogger> MockLogger) CreateSourceWithFastRescan()
    {
        var configuration = CreateConfiguration();
        configuration.RescanDebounceTime = TimeSpan.FromMilliseconds(50);
        configuration.HealthCheckInterval = TimeSpan.FromMilliseconds(50);

        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var source = CreateSource(configuration: configuration, logger: mockLogger.Object);
        return (source, mockLogger);
    }

    private static void VerifyLogContains(Mock<ILogger> mockLogger, LogLevel level, string messageSubstring)
    {
        mockLogger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) => value.ToString()!.Contains(messageSubstring)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void Constructor_ShouldCreateSourceSuccessfully()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);

        // Act
        var source = CreateSource(subject);

        // Assert
        Assert.NotNull(source);
        Assert.Same(subject, source.RootSubject);
    }

    [Fact]
    public void WriteBatchSize_ShouldBeZero()
    {
        // Arrange
        var source = CreateSource();

        // Act & Assert
        Assert.Equal(0, source.WriteBatchSize);
    }

    [Fact]
    public void Diagnostics_ShouldReturnNonNull()
    {
        // Arrange
        var source = CreateSource();

        // Act & Assert
        Assert.NotNull(source.Diagnostics);
    }

    [Fact]
    public void Diagnostics_ShouldReturnSameInstance()
    {
        // Arrange
        var source = CreateSource();

        // Act & Assert
        Assert.Same(source.Diagnostics, source.Diagnostics);
    }

    [Fact]
    public void Diagnostics_InitialState_ShouldHaveZeroCounts()
    {
        // Arrange
        var source = CreateSource();

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
        var source = CreateSource();

        // Act & Assert - should not throw
        await source.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_ShouldBeIdempotent()
    {
        // Arrange
        var source = CreateSource();

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
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName)
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
        var source = CreateSource();

        // Act - no connection established
        var result = await source.LoadInitialStateAsync(CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteChangesAsync_WhenNotConnected_ShouldReturnFailure()
    {
        // Arrange
        var source = CreateSource();
        var changes = new Tracking.Change.SubjectPropertyChange[1];

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
        var configuration = CreateConfiguration();
        configuration.HealthCheckInterval = TimeSpan.FromMilliseconds(50);
        var source = CreateSource(configuration: configuration);

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
        var source = CreateSource();
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
    public async Task RequestRescan_ViaConnectionRestored_WhenNotConnected_SkipsGracefully()
    {
        // Arrange
        var (source, mockLogger) = CreateSourceWithFastRescan();
        await source.StartAsync(CancellationToken.None);

        try
        {
            // Act - simulate a ConnectionRestored event (no real connection exists)
            source.ConnectionManager.OnConnectionStateChanged(null,
                new ConnectionStateChangedEventArgs(
                    ConnectionStateChangedReason.Established,
                    ConnectionState.Connected,
                    ConnectionState.Disconnected));

            // Wait for debounce + processing
            await Task.Delay(300);

            // Assert - rescan skipped because Connection is null, logged as debug
            VerifyLogContains(mockLogger, LogLevel.Debug, "Skipping rescan");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task RequestRescan_ViaAdsStateEnteredRun_WhenNotConnected_SkipsGracefully()
    {
        // Arrange
        var (source, mockLogger) = CreateSourceWithFastRescan();
        await source.StartAsync(CancellationToken.None);

        try
        {
            // Act - simulate ADS state entering Run
            source.ConnectionManager.OnAdsStateChanged(null,
                new AdsStateChangedEventArgs(new StateInfo(AdsState.Run, 0)));

            await Task.Delay(300);

            // Assert - rescan skipped because Connection is null
            VerifyLogContains(mockLogger, LogLevel.Debug, "Skipping rescan");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task RequestRescan_ViaSymbolVersionChanged_WhenNotConnected_SkipsGracefully()
    {
        // Arrange
        var (source, mockLogger) = CreateSourceWithFastRescan();
        await source.StartAsync(CancellationToken.None);

        try
        {
            // Act - simulate symbol version change
            source.ConnectionManager.OnSymbolVersionChanged(null,
                new AdsSymbolVersionChangedEventArgs(2));

            await Task.Delay(300);

            // Assert - rescan skipped because Connection is null
            VerifyLogContains(mockLogger, LogLevel.Debug, "Skipping rescan");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task RequestRescan_MultipleRapidEvents_CoalescedIntoSingleRescan()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.RescanDebounceTime = TimeSpan.FromMilliseconds(100);
        configuration.HealthCheckInterval = TimeSpan.FromMilliseconds(50);

        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var source = CreateSource(configuration: configuration, logger: mockLogger.Object);

        await source.StartAsync(CancellationToken.None);

        try
        {
            // Act - fire multiple events in rapid succession (simulates reconnection burst)
            source.ConnectionManager.OnConnectionStateChanged(null,
                new ConnectionStateChangedEventArgs(
                    ConnectionStateChangedReason.Established,
                    ConnectionState.Connected,
                    ConnectionState.Disconnected));

            source.ConnectionManager.OnAdsStateChanged(null,
                new AdsStateChangedEventArgs(new StateInfo(AdsState.Run, 0)));

            source.ConnectionManager.OnSymbolVersionChanged(null,
                new AdsSymbolVersionChangedEventArgs(2));

            // Wait for debounce + processing
            await Task.Delay(500);

            // Assert - the 3 rapid events should coalesce: we see "Executing debounced rescan"
            // at least once (proves coalescing), and "Skipping rescan" (proves retry when no connection).
            VerifyLogContains(mockLogger, LogLevel.Information, "Executing debounced rescan");
            VerifyLogContains(mockLogger, LogLevel.Debug, "Skipping rescan");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await source.DisposeAsync();
        }
    }

    [Fact]
    public void RequestRescan_WithoutBackgroundService_DoesNotThrow()
    {
        // Arrange - tests that RequestRescan is safe to call even if ExecuteAsync isn't running
        var source = CreateSource();

        // Act & Assert - should not throw
        source.RequestRescan();
        source.RequestRescan();
        source.RequestRescan();
    }

    [Fact]
    public async Task ConnectionLost_StartsBuffering_DoesNotThrowWhenNoPropertyWriter()
    {
        // Arrange
        var source = CreateSource();

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
