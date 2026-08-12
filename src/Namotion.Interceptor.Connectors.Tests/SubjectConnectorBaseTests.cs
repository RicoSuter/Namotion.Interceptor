using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectConnectorBaseTests
{
    [Fact]
    public async Task WhenStarted_ThenStartTimeIsStamped()
    {
        // Arrange
        using var connector = new TestConnector();

        // Act
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime is not null);

        // Assert
        Assert.NotNull(connector.Diagnostics.StartTime);
    }

    [Fact]
    public async Task WhenRunAsyncFaults_ThenTheErrorIsRecordedAndTheConnectorIsNotOperational()
    {
        // Arrange
        var error = new InvalidOperationException("run failed");
        using var connector = new TestConnector { Fault = error };
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        Assert.True(connector.Diagnostics.IsOperational);

        // Act
        connector.Release();

        // Awaiting the execute task rather than polling: ExecuteAsync's finally has run by the time
        // that task completes, so the liveness this asserts on is already settled.
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ExecuteTask!);

        // Assert
        Assert.Same(error, connector.Diagnostics.LastError);
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenStoppedWhileBackingOffFromAFault_ThenTheFaultIsStillTheReportedError()
    {
        // Arrange
        var error = new InvalidOperationException("connect failed");
        using var connector = new TestConnector { FaultBeforeBackoff = error };
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.LastError is not null);

        // Act
        await ((IHostedService)connector).StopAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ExecuteTask!);

        // Assert
        Assert.Same(error, connector.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenStoppedAfterBeingOperational_ThenItReportsNotOperational()
    {
        // Arrange
        using var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.IsOperational);

        // Act
        connector.Release();
        await ((IHostedService)connector).StopAsync(CancellationToken.None);

        // Assert
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenDisposedWithoutStopping_ThenItReportsNotOperational()
    {
        // Arrange
        var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.IsOperational);

        // Act
        connector.Dispose();

        // Assert
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenReEntered_ThenTheEpochMovesAndTotalsReset()
    {
        // Arrange
        using var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime is not null);
        var firstStart = connector.Diagnostics.StartTime;
        connector.AddOutboundDrop(5);
        connector.Release();
        await ((IHostedService)connector).StopAsync(CancellationToken.None);

        // Act
        ClockTestHelpers.WaitForClockTick();
        connector.Reopen();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime != firstStart);

        // Assert
        Assert.NotEqual(firstStart, connector.Diagnostics.StartTime);
        Assert.Equal(0, connector.Diagnostics.OutboundChanges.TotalDropped);
    }

    [Fact]
    public async Task WhenReEnteredAfterStopping_ThenItCanReportOperationalAgain()
    {
        // Arrange
        using var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        connector.Release();
        await ((IHostedService)connector).StopAsync(CancellationToken.None);
        Assert.False(connector.Diagnostics.IsOperational);

        // Act
        connector.Reopen();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();

        // Assert
        Assert.True(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public void WhenReadThroughTheConnectorInterface_ThenItIsTheConnectorsOwnDiagnostics()
    {
        // Arrange
        using var connector = new TestConnector();

        // Act
        var throughInterface = ((ISubjectConnector)connector).Diagnostics;

        // Assert
        // The base declares the member as ConnectorDiagnostics and derived connectors narrow it with a
        // covariant override, so the interface has to land on that same override rather than on a
        // second diagnostics view reading metrics nobody writes to.
        Assert.Same(connector.Diagnostics, throughInterface);
    }

    private sealed class TestConnector : SubjectConnectorBase
    {
        private readonly ConnectorMetrics _metrics;
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestConnector()
            : this(new ConnectorMetrics())
        {
        }

        private TestConnector(ConnectorMetrics metrics)
            : base(metrics)
        {
            _metrics = metrics;
            Diagnostics = new ConnectorDiagnostics(metrics);
        }

        public Exception? Fault { get; init; }

        public Exception? FaultBeforeBackoff { get; init; }

        public override IInterceptorSubject RootSubject => throw new NotSupportedException();

        public override ConnectorDiagnostics Diagnostics { get; }

        public void MarkOperational() => _metrics.MarkOperational();

        public void AddOutboundDrop(long count) => _metrics.OutboundChanges.AddDropped(count);

        public void Release() => _gate.TrySetResult();

        public void Reopen() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task RunAsync(CancellationToken stoppingToken)
        {
            if (FaultBeforeBackoff is not null)
            {
                // Mirrors a connector that records its own connect failure and then backs off inside
                // its own catch block. The backoff never elapses, so the only way out is the stopping
                // token, which throws the cancellation out of RunAsync past the clause that would
                // otherwise have swallowed it.
                _metrics.ReportError(FaultBeforeBackoff);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }

            await using (stoppingToken.Register(() => _gate.TrySetResult()))
            {
                await _gate.Task;
            }

            // Faulting only once the gate opens keeps the failure asynchronous, which is the case the
            // base class exists for. BackgroundService.StartAsync hands its execute task back to the
            // caller when that task has already completed, so a RunAsync that faults before the check
            // surfaces the exception from StartAsync instead of from the running connector, and the
            // check races the fault so neither outcome can be asserted reliably.
            if (Fault is not null)
            {
                throw Fault;
            }
        }
    }
}
