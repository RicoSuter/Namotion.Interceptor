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
        using var registration = metrics.Register(() => depth, dropped: null, capacity: 100);

        // Assert
        Assert.Equal(7, diagnostics.Depth);
        Assert.Equal(100, diagnostics.Capacity);
    }

    [Fact]
    public void WhenRegistrationHandleIsDisposed_ThenDepthReturnsToZeroButCapacityStays()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var registration = metrics.Register(() => 7, dropped: null, capacity: 100);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        registration.Dispose();

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
        using var registration = metrics.Register(() => 0, () => live, capacity: 10);
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
        var firstRegistration = metrics.Register(() => 0, () => first, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        Assert.Equal(5, diagnostics.TotalDropped);

        // Act
        firstRegistration.Dispose();
        var afterFirstRegistrationIsDisposed = diagnostics.TotalDropped;

        var second = 0L;
        using var secondRegistration = metrics.Register(() => 0, () => second, capacity: 10);
        var afterSecondRegistration = diagnostics.TotalDropped;
        second = 3;

        // Assert
        Assert.Equal(5, afterFirstRegistrationIsDisposed);
        Assert.Equal(5, afterSecondRegistration);
        Assert.Equal(8, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenAddDroppedRacesWithRegistrationDisposal_ThenNoIncrementIsLost()
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
                metrics.Register(() => 0, dropped: null, capacity: 10).Dispose();
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
            var registration = metrics.Register(() => 0, () => live, capacity: 10);

            // Stepping instead of jumping to the final count widens the window in which a broken
            // handover shows up as a decrease. Volatile.Write keeps the JIT from collapsing the
            // stores into one, which would undo that widening.
            for (var step = 1; step <= 4; step++)
            {
                Volatile.Write(ref live, step);
            }

            registration.Dispose();
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
        using var registration = metrics.Register(() => 4, dropped: null, capacity: 10);
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
        var firstRegistration = metrics.Register(() => 0, () => first, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        metrics.Reset();
        Assert.Equal(0, diagnostics.TotalDropped);

        // Act: three more drops arrive on the still-live first provider after the reset, then it is
        // handed over through the normal registration-handle disposal cycle.
        first = 8;
        firstRegistration.Dispose();
        var second = 0L;
        using var secondRegistration = metrics.Register(() => 0, () => second, capacity: 10);

        // Assert
        Assert.Equal(3, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenRegisterIsCalledWhileARegistrationIsLive_ThenItThrowsAndHandleDisposalAllowsRegisteringAgain()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var firstRegistration = metrics.Register(() => 0, dropped: null, capacity: 10);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => metrics.Register(() => 0, dropped: null, capacity: 20));

        // Act
        firstRegistration.Dispose();
        using var secondRegistration = metrics.Register(() => 3, dropped: null, capacity: 30);
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
        using var registration = metrics.OutboundRetries.Register(() => 0, dropped: null, capacity: 10);

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
            using var registration = metrics.Register(() => 7, dropped: null, capacity: null);

            Assert.Equal(7, diagnostics.Depth);
        }
    }

    [Fact]
    public void WhenDisposedHandleIsDisposedAgainAfterReplacement_ThenReplacementRemainsRegistered()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var firstRegistration = metrics.Register(() => 7, dropped: null, capacity: 10);
        firstRegistration.Dispose();
        using var secondRegistration = metrics.Register(() => 3, dropped: null, capacity: 20);

        // Act
        firstRegistration.Dispose();

        // Assert
        Assert.Equal(3, diagnostics.Depth);
        Assert.Equal(20, diagnostics.Capacity);
    }

    [Fact]
    public void WhenRegisterThrows_ThenTheLiveRegistrationIsUntouched()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        using var registration = metrics.Register(() => 7, dropped: null, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => metrics.Register(() => 3, dropped: null, capacity: 20));

        Assert.Equal(7, diagnostics.Depth);
        Assert.Equal(10, diagnostics.Capacity);
    }

    [Fact]
    public void WhenTheHandleIsDisposedTwice_ThenProvidersAreReleasedAndDropsAreFoldedOnce()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var diagnostics = new QueueDiagnostics(metrics);
        var dropped = 0L;
        var registration = metrics.Register(() => 7, () => dropped, capacity: 10);
        dropped = 4;

        // Act
        registration.Dispose();
        var totalDroppedAfterFirstDisposal = diagnostics.TotalDropped;
        registration.Dispose();

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Equal(4, totalDroppedAfterFirstDisposal);
        Assert.Equal(4, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenProviderThrowsAfterReset_ThenTotalDroppedNeverGoesNegative()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        var shouldThrow = false;
        var live = 5L;
        using var registration = metrics.Register(() => 0, () => shouldThrow ? throw new InvalidOperationException("boom") : live, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        metrics.Reset();
        Assert.Equal(0, diagnostics.TotalDropped);

        // Act: the provider starts throwing instead of being handed over, so Reset's negated
        // accumulator is left uncancelled.
        shouldThrow = true;

        // Assert
        Assert.Equal(0, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenProvidersThrow_ThenDiagnosticsReportZero()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        using var registration = metrics.Register(
            () => throw new InvalidOperationException("boom"),
            () => throw new InvalidOperationException("boom"),
            capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        var depth = diagnostics.Depth;
        var totalDropped = diagnostics.TotalDropped;

        // Assert
        Assert.Equal(0, depth);
        Assert.Equal(0, totalDropped);
    }

    [Fact]
    public async Task WhenRegistrationsRace_ThenExactlyOneRegistrationSucceeds()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(ConnectorMetrics.OutboundChanges));
        const int registrationCount = 16;
        using var barrier = new Barrier(registrationCount + 1);
        var registrations = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<InvalidOperationException>();

        var attempts = Enumerable.Range(0, registrationCount)
            .Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();

                try
                {
                    registrations.Add(metrics.Register(() => 0, dropped: null, capacity: 10));
                }
                catch (InvalidOperationException exception)
                {
                    failures.Add(exception);
                }
            }))
            .ToArray();

        // Act
        barrier.SignalAndWait();
        await Task.WhenAll(attempts);

        // Assert
        Assert.Single(registrations);
        Assert.Equal(registrationCount - 1, failures.Count);

        // Cleanup
        registrations.Single().Dispose();
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
