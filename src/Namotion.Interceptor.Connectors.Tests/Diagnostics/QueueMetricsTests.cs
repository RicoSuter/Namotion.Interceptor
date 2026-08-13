using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class QueueMetricsTests
{
    [Fact]
    public void WhenNothingIsRegistered_ThenDepthIsZeroAndCapacityIsNull()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));

        // Act
        var diagnostics = new QueueDiagnostics(metrics);

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Null(diagnostics.Capacity);
        Assert.Equal(0, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenProviderIsRegistered_ThenDepthAndCapacityComeFromIt()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var depth = 7;
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        metrics.Register(() => depth, dropped: null, capacity: 100);

        // Assert
        Assert.Equal(7, diagnostics.Depth);
        Assert.Equal(100, diagnostics.Capacity);
    }

    [Fact]
    public void WhenProviderIsDeregistered_ThenDepthReturnsToZeroButCapacityStays()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        metrics.Register(() => 7, dropped: null, capacity: 100);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        metrics.Deregister();

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Equal(100, diagnostics.Capacity);
    }

    [Fact]
    public void WhenLiveProviderReportsDrops_ThenTotalAdvancesDuringTheBurst()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var live = 0L;
        metrics.Register(() => 0, () => live, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        live = 5;

        // Assert
        Assert.Equal(5, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenProviderIsHandedOver_ThenTotalNeitherDecreasesNorDoubleCounts()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var first = 5L;
        metrics.Register(() => 0, () => first, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        Assert.Equal(5, diagnostics.TotalDropped);

        // Act
        metrics.Deregister();
        var afterDeregister = diagnostics.TotalDropped;

        var second = 0L;
        metrics.Register(() => 0, () => second, capacity: 10);
        var afterReregister = diagnostics.TotalDropped;
        second = 3;

        // Assert
        Assert.Equal(5, afterDeregister);
        Assert.Equal(5, afterReregister);
        Assert.Equal(8, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenAddDroppedRacesWithDeregister_ThenNoIncrementIsLost()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        const int iterations = 10_000;

        // Act
        var adder = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                metrics.AddDropped(1);
            }
        });

        var churner = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                metrics.Register(() => 0, dropped: null, capacity: 10);
                metrics.Deregister();
            }
        });

        await Task.WhenAll(adder, churner);

        // Assert
        Assert.Equal(iterations, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenTotalIsReadRepeatedlyDuringChurn_ThenItNeverDecreases()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var stop = false;
        var observed = 0L;
        var decreased = false;

        // Act
        var reader = Task.Run(() =>
        {
            // Only this task writes observed and decreased, and the await below establishes the
            // happens-before ordering, so plain reads and writes are enough here.
            while (!Volatile.Read(ref stop))
            {
                var current = diagnostics.TotalDropped;
                if (current < observed)
                {
                    decreased = true;
                }

                observed = current;
            }
        });

        for (var i = 0; i < 500; i++)
        {
            var live = 0L;
            metrics.Register(() => 0, () => live, capacity: 10);

            // Stepping instead of jumping to the final count widens the window in which a broken
            // handover shows up as a decrease. Volatile.Write keeps the JIT from collapsing the
            // stores into one, which would undo that widening.
            for (var step = 1; step <= 4; step++)
            {
                Volatile.Write(ref live, step);
            }

            metrics.Deregister();
        }

        Volatile.Write(ref stop, true);
        await reader;

        // Assert
        Assert.False(decreased);
        Assert.Equal(2000, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenReset_ThenTotalDroppedReturnsToZeroAndRegistrationSurvives()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        metrics.AddDropped(9);
        metrics.Register(() => 4, dropped: null, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, diagnostics.TotalDropped);
        Assert.Equal(4, diagnostics.Depth);
        Assert.Equal(10, diagnostics.Capacity);
    }

    [Fact]
    public void WhenProviderIsReplacedAfterReset_ThenTotalDroppedKeepsTheDropsAndNeverGoesNegative()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var first = 5L;
        metrics.Register(() => 0, () => first, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        metrics.Reset();
        Assert.Equal(0, diagnostics.TotalDropped);

        // Act: three more drops arrive on the still-live first provider after the reset, then it is
        // handed over through the normal Deregister/Register cycle.
        first = 8;
        metrics.Deregister();
        var second = 0L;
        metrics.Register(() => 0, () => second, capacity: 10);

        // Assert
        Assert.Equal(3, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenRegisterIsCalledWhileARegistrationIsLive_ThenItThrowsAndDeregisterAllowsRegisteringAgain()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        metrics.Register(() => 0, dropped: null, capacity: 10);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => metrics.Register(() => 0, dropped: null, capacity: 20));

        // Act
        metrics.Deregister();
        metrics.Register(() => 3, dropped: null, capacity: 30);
        var diagnostics = new QueueDiagnostics(metrics);

        // Assert
        Assert.Equal(3, diagnostics.Depth);
        Assert.Equal(30, diagnostics.Capacity);
    }

    [Fact]
    public void WhenRegisterIsCalledWhileARegistrationIsLive_ThenTheMessageNamesTheBuffer()
    {
        // Arrange: the failure surfaces from inside a connector's retry loop, which catches it and
        // tries again, so the message is all an operator gets.
        var metrics = new SourceMetrics();
        metrics.OutboundRetries.Register(() => 0, dropped: null, capacity: 10);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => metrics.OutboundRetries.Register(() => 0, dropped: null, capacity: 20));

        Assert.Contains(nameof(SourceMetrics.OutboundRetries), exception.Message);
    }

    [Fact]
    public void WhenTheHandleIsDeclaredAfterTheBuffer_ThenTheRegistrationIsReleasedBeforeTheBufferGoesAway()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var depthSeenByTheBuffer = -1;

        // Act
        RunScope();

        // Assert
        Assert.Equal(0, depthSeenByTheBuffer);

        void RunScope()
        {
            using var buffer = new CallbackDisposable(() => depthSeenByTheBuffer = diagnostics.Depth);
            using var registration = metrics.BeginRegister(() => 7, dropped: null, capacity: null);

            Assert.Equal(7, diagnostics.Depth);
        }
    }

    [Fact]
    public void WhenTheHandleIsDisposedTwice_ThenTheSecondDisposalLeavesALaterRegistrationAlone()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var registration = metrics.BeginRegister(() => 7, dropped: null, capacity: 10);
        registration.Dispose();
        metrics.Register(() => 3, dropped: null, capacity: 20);

        // Act
        registration.Dispose();

        // Assert
        Assert.Equal(3, diagnostics.Depth);
        Assert.Throws<InvalidOperationException>(() => metrics.Register(() => 0, dropped: null, capacity: 30));
    }

    [Fact]
    public void WhenBeginRegisterThrows_ThenTheLiveRegistrationIsUntouched()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        metrics.Register(() => 7, dropped: null, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => metrics.BeginRegister(() => 3, dropped: null, capacity: 20));

        Assert.Equal(7, diagnostics.Depth);
        Assert.Equal(10, diagnostics.Capacity);
    }

    [Fact]
    public void WhenTheHandleIsDisposed_ThenTheProvidersAreReleasedAndTheirDropsKept()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var dropped = 0L;
        var registration = metrics.BeginRegister(() => 7, () => dropped, capacity: 10);
        dropped = 4;

        // Act
        registration.Dispose();

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Equal(4, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenProviderThrowsAfterReset_ThenTotalDroppedNeverGoesNegative()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var shouldThrow = false;
        var live = 5L;
        metrics.Register(() => 0, () => shouldThrow ? throw new InvalidOperationException("boom") : live, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        metrics.Reset();
        Assert.Equal(0, diagnostics.TotalDropped);

        // Act: the provider starts throwing instead of being handed over, so Reset's negated
        // accumulator is left uncancelled.
        shouldThrow = true;

        // Assert
        Assert.Equal(0, diagnostics.TotalDropped);
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
