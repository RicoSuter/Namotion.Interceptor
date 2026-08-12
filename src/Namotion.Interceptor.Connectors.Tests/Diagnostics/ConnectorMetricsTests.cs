using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class ConnectorMetricsTests
{
    [Fact]
    public void WhenNeverStarted_ThenNotOperationalAndNoTimestamps()
    {
        // Arrange
        var metrics = new ConnectorMetrics();

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.StartTime);
        Assert.Null(diagnostics.LastError);
    }

    [Fact]
    public void WhenMarkedOperational_ThenFlagAndTimestampMoveTogether()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkStarted();

        // Act
        metrics.MarkOperational();

        // Assert
        Assert.True(diagnostics.IsOperational);
        Assert.NotNull(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenMarkedOperationalTwice_ThenTheTimestampDoesNotMove()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        var first = diagnostics.OperationalChangeTime;

        // Act
        metrics.MarkOperational();

        // Assert
        Assert.Equal(first, diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenMarkedNotOperational_ThenTheTimestampMovesOnTheDownTransition()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        var whenUp = diagnostics.OperationalChangeTime;

        // Act
        WaitForClockTick();
        metrics.MarkNotOperational();

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.NotNull(whenUp);
        Assert.True(diagnostics.OperationalChangeTime > whenUp);
    }

    [Fact]
    public void WhenStopped_ThenLaterMarkOperationalIsIgnored()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();

        // Act
        metrics.MarkStopped();
        metrics.MarkOperational();

        // Assert
        Assert.False(diagnostics.IsOperational);
    }

    [Fact]
    public void WhenStoppedTwice_ThenTheSecondCallLeavesStateAndTimestampUnchanged()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        metrics.MarkStopped();
        var afterFirstStop = diagnostics.OperationalChangeTime;

        // Act
        WaitForClockTick();
        metrics.MarkStopped();

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Equal(afterFirstStop, diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenStoppedWithoutEverBeingOperational_ThenNoTransitionTimestampIsInvented()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Act
        metrics.MarkStopped();

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenErrorIsReported_ThenItIsStickyAcrossRecovery()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        var error = new InvalidOperationException("boom");

        // Act
        metrics.ReportError(error);
        metrics.MarkOperational();

        // Assert
        Assert.Same(error, diagnostics.LastError);
    }

    [Fact]
    public void WhenRestarted_ThenLastErrorIsClearedThoughRecoveryAloneDoesNotClearIt()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.ReportError(new InvalidOperationException("boom"));

        // Act
        metrics.MarkOperational();
        var afterRecovery = diagnostics.LastError;
        metrics.MarkStarted();

        // Assert
        Assert.NotNull(afterRecovery);
        Assert.Null(diagnostics.LastError);
    }

    [Fact]
    public void WhenRestarted_ThenStartTimeMovesAndEveryTotalResets()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        var hoisted = new CountingResettable();
        metrics.RegisterResettable(hoisted);

        metrics.MarkStarted();
        var firstStart = diagnostics.StartTime;
        metrics.OutboundChanges.AddDropped(3);
        metrics.OutboundRetries.AddDropped(4);
        metrics.InboundBuffer.AddDropped(5);

        // Act
        WaitForClockTick();
        metrics.MarkStarted();

        // Assert
        Assert.NotEqual(firstStart, diagnostics.StartTime);
        Assert.Equal(0, diagnostics.OutboundChanges.TotalDropped);
        Assert.Equal(0, diagnostics.OutboundRetries.TotalDropped);
        Assert.Equal(0, diagnostics.InboundBuffer.TotalDropped);

        // Once per MarkStarted, including the one in the arrange: the epoch is deliberately not
        // idempotent, so every call resets the hoisted metrics too.
        Assert.Equal(2, hoisted.ResetCount);
    }

    [Fact]
    public void WhenNoThroughputCountersArePassed_ThenBothRatesAreNull()
    {
        // Arrange
        var metrics = new ConnectorMetrics();

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Assert
        Assert.Null(diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public void WhenOnlyIncomingIsInstrumented_ThenOutgoingStaysNullAndIncomingReportsZeroWhenIdle()
    {
        // Arrange
        var metrics = new ConnectorMetrics(incoming: new ThroughputCounter());

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Assert
        Assert.Equal(0.0, diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public void WhenNoClaimedPropertyProviderIsRegistered_ThenCountIsZero()
    {
        // Arrange
        var metrics = new SourceMetrics();

        // Act
        var diagnostics = new SourceDiagnostics(metrics);

        // Assert
        Assert.Equal(0, diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public void WhenClaimedPropertyProviderIsRegistered_ThenCountFollowsIt()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var count = 0;
        metrics.RegisterClaimedProperties(() => count);
        var diagnostics = new SourceDiagnostics(metrics);

        // Act
        count = 42;

        // Assert
        Assert.Equal(42, diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public void WhenClaimedPropertyProviderThrows_ThenCountIsZeroInsteadOfThrowing()
    {
        // Arrange
        var metrics = new SourceMetrics();
        metrics.RegisterClaimedProperties(() => throw new InvalidOperationException("boom"));
        var diagnostics = new SourceDiagnostics(metrics);

        // Act
        var count = diagnostics.ClaimedPropertyCount;

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void WhenNullIsRegisteredOrReported_ThenTheGuardNamesTheParameter()
    {
        // Arrange
        var metrics = new SourceMetrics();

        // Act & Assert
        Assert.Equal("metrics", Assert.Throws<ArgumentNullException>(() => metrics.RegisterResettable(null!)).ParamName);
        Assert.Equal("error", Assert.Throws<ArgumentNullException>(() => metrics.ReportError(null!)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentNullException>(() => metrics.RegisterClaimedProperties(null!)).ParamName);
    }

    [Fact]
    public async Task WhenLivenessIsFlippedConcurrently_ThenTheChangeTimestampNeverMovesBackwards()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        var stop = false;
        var movedBackwards = false;
        metrics.MarkOperational();

        // Act
        // Both threads flip the liveness: a stale timestamp can only be stamped by a writer that lost
        // the race to another writer, so a reader beside a single writer would never observe one.
        var flipper = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                metrics.MarkNotOperational();
                metrics.MarkOperational();
            }
        });

        var previous = DateTimeOffset.MinValue;
        for (var i = 0; i < 100_000; i++)
        {
            metrics.MarkNotOperational();
            metrics.MarkOperational();

            // What the record guarantees is that each individual read is internally consistent and
            // that the timestamp only ever moves forward. It does not make IsOperational and
            // OperationalChangeTime one atomic pair: they are two separate reads of two separate
            // snapshots, so a reader that needs a coherent flag-and-timestamp pair cannot get it by
            // reading both properties.
            var changeTime = diagnostics.OperationalChangeTime;
            if (changeTime < previous)
            {
                movedBackwards = true;
            }

            previous = changeTime ?? previous;
        }

        Volatile.Write(ref stop, true);
        await flipper;

        // Assert
        Assert.False(movedBackwards);
    }

    // Spins until the wall clock reports a new tick, so a second MarkStarted cannot land on the same
    // timestamp as the first. A condition rather than a fixed delay, because the clock's resolution
    // differs per platform.
    private static void WaitForClockTick()
    {
        var start = DateTimeOffset.UtcNow.UtcTicks;

        SpinWait spin = default;
        while (DateTimeOffset.UtcNow.UtcTicks == start)
        {
            spin.SpinOnce();
        }
    }

    private sealed class CountingResettable : IResettableMetrics
    {
        public int ResetCount { get; private set; }

        public void Reset() => ResetCount++;
    }
}
