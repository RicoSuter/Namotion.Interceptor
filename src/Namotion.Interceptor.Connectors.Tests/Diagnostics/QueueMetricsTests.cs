using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class QueueMetricsTests
{
    [Fact]
    public void WhenNothingIsRegistered_ThenDepthIsZeroAndCapacityIsNull()
    {
        // Arrange
        var metrics = new QueueMetrics();

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
        var metrics = new QueueMetrics();
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
        var metrics = new QueueMetrics();
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
        var metrics = new QueueMetrics();
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
        var metrics = new QueueMetrics();
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
        var metrics = new QueueMetrics();
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
        var metrics = new QueueMetrics();
        var diagnostics = new QueueDiagnostics(metrics);
        var stop = false;
        var observed = 0L;
        var decreased = false;

        // Act
        var reader = Task.Run(() =>
        {
            // Only this task writes observed/decreased while it runs; the main thread only reads
            // decreased below, after awaiting this task, and that await is what establishes the
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

            // Advance across several values while the reader is concurrently polling, instead of
            // jumping straight to the final count, so the reader has an actual chance to observe a
            // decrease if the handover were broken. Volatile.Write keeps the JIT from collapsing
            // these stores into one, which would quietly undo the widening and make the test
            // non-deterministic.
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
        var metrics = new QueueMetrics();
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
        var metrics = new QueueMetrics();
        var first = 5L;
        metrics.Register(() => 0, () => first, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        metrics.Reset();
        Assert.Equal(0, diagnostics.TotalDropped);

        // Act

        // Three more drops arrive on the still-live first provider after the reset, then it is
        // handed over to a second provider through the normal Deregister/Register cycle.
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
        var metrics = new QueueMetrics();
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
    public void WhenProviderThrowsAfterReset_ThenTotalDroppedNeverGoesNegative()
    {
        // Arrange
        var metrics = new QueueMetrics();
        var shouldThrow = false;
        var live = 5L;
        metrics.Register(() => 0, () => shouldThrow ? throw new InvalidOperationException("boom") : live, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        metrics.Reset();
        Assert.Equal(0, diagnostics.TotalDropped);

        // Act

        // The still-live provider starts throwing instead of being handed over, so Reset's negated
        // accumulator has nothing to be cancelled by.
        shouldThrow = true;

        // Assert
        Assert.Equal(0, diagnostics.TotalDropped);
    }
}
