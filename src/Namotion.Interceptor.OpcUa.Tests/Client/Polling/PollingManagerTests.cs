using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client.Polling;

public class PollingManagerTests
{
    [Fact]
    public async Task WhenPollingItemsMutate_ThenCachedCountStaysCurrent()
    {
        // Arrange
        await using var source = CreateClientSource();
        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var nodeId = new NodeId("Name", 2);
        await using var manager = new PollingManager(
            source,
            sessionProvider: () => null,
            new SubjectPropertyWriter(source, NullLogger.Instance),
            CreateConfiguration(),
            new PollingMetrics(),
            reportError: static _ => { },
            NullLogger.Instance);

        // Act & Assert
        Assert.Equal(0, manager.PollingItemCount);
        manager.AddItem(nodeId, property);
        Assert.Equal(1, manager.PollingItemCount);
        manager.AddItem(nodeId, property);
        Assert.Equal(1, manager.PollingItemCount);
        manager.RemoveItemsForSubject(source.RootSubject);
        Assert.Equal(0, manager.PollingItemCount);
        manager.AddItem(nodeId, property);
        Assert.Equal(1, manager.PollingItemCount);
        manager.Clear();
        Assert.Equal(0, manager.PollingItemCount);
        manager.AddItem(nodeId, property);
        Assert.Equal(1, manager.PollingItemCount);
        await manager.DisposeAsync();
        Assert.Equal(0, manager.PollingItemCount);
        manager.AddItem(nodeId, property);
        Assert.Equal(0, manager.PollingItemCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenShutdownEndsABatchRead_ThenItDoesNotRecordPollingFailures(
        bool throwOperationCanceledException)
    {
        // Arrange
        await using var source = CreateClientSource();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long readStartedTimestamp = 0;
        var reportedErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        async Task<ReadResponse> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref readStartedTimestamp, Stopwatch.GetTimestamp());
            readStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellation wait completed without cancellation.");
            }
            catch (OperationCanceledException) when (!throwOperationCanceledException)
            {
                throw new ObjectDisposedException(nameof(ISession));
            }
        }

        var session = new Mock<ISession>();
        session.SetupGet(value => value.Connected).Returns(true);
        session
            .Setup(value => value.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ArrayOf<ReadValueId>>(),
                It.IsAny<CancellationToken>()))
            .Returns((RequestHeader _, double _, TimestampsToReturn _, ArrayOf<ReadValueId> _, CancellationToken cancellationToken) =>
                new ValueTask<ReadResponse>(WaitForCancellationAsync(cancellationToken)));

        var configuration = CreateConfiguration();
        configuration.PollingInterval = TimeSpan.FromMilliseconds(1);
        configuration.PollingCircuitBreakerThreshold = 1;
        configuration.PollingDisposalTimeout = TestTimeouts.Default;

        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var nodeId = new NodeId("Name", 2);

        var metrics = new PollingMetrics();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = new PollingManager(
            source,
            sessionProvider: () => session.Object,
            propertyWriter,
            configuration,
            metrics,
            reportedErrors.Enqueue,
            NullLogger.Instance);
        manager.AddItem(nodeId, property);

        // Act
        manager.Start();
        await readStarted.Task.WaitAsync(TestTimeouts.Default);
        await AsyncTestHelpers.WaitUntilAsync(
            () => Stopwatch.GetElapsedTime(Volatile.Read(ref readStartedTimestamp)) > configuration.PollingInterval,
            pollInterval: TimeSpan.FromMilliseconds(1));
        await manager.DisposeAsync();

        // Assert
        Assert.Equal(
            (FailedReads: 0L, SlowPolls: 0L, CircuitBreakerTrips: 0L, IsCircuitOpen: false),
            (metrics.FailedReads, metrics.SlowPolls, metrics.CircuitBreakerTrips, manager.IsCircuitOpen));
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenBatchReadThrows_ThenSourceReportsFailureOnceAndKeepsItAfterRecovery()
    {
        // Arrange
        await using var source = CreateClientSource();
        var error = new InvalidOperationException("poll failed");
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        void ReportError(Exception exception)
        {
            reportedErrors.Enqueue(exception);
            source.ReportBackgroundError(exception);
            reported.TrySetResult();
        }

        var session = new Mock<ISession>();
        session.SetupGet(value => value.Connected).Returns(true);
        var readAttempts = 0;
        var goodResponse = new ReadResponse
        {
            ResponseHeader = new ResponseHeader(),
            Results =
            [
                new DataValue("recovered", StatusCodes.Good, DateTime.UtcNow)
            ],
            DiagnosticInfos = []
        };
        session
            .Setup(value => value.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ArrayOf<ReadValueId>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref readAttempts) == 1
                ? ValueTask.FromException<ReadResponse>(error)
                : ValueTask.FromResult(goodResponse));

        var configuration = CreateConfiguration();
        configuration.PollingInterval = TimeSpan.FromMilliseconds(100);
        configuration.PollingDisposalTimeout = TestTimeouts.Default;

        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var nodeId = new NodeId("Name", 2);

        var metrics = new PollingMetrics();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = new PollingManager(
            source,
            sessionProvider: () => session.Object,
            propertyWriter,
            configuration,
            metrics,
            ReportError,
            NullLogger.Instance);
        manager.AddItem(nodeId, property);

        // Act
        manager.Start();
        await reported.Task.WaitAsync(TestTimeouts.Default);
        await manager.DisposeAsync();

        // Assert
        var errors = reportedErrors.ToArray();
        Assert.All(errors, reportedError => Assert.Same(error, reportedError));
        Assert.Single(errors);
        Assert.Same(error, source.Diagnostics.LastError);

        source.NotifySessionHealthy();
        Assert.Same(error, source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenTheServerLimitsNodesPerRead_ThenEachPolledReadStaysWithinItAndAMismatchedBatchFails()
    {
        // Arrange
        // Past the limit the SDK splits a read itself and only its last request would be checked against
        // the request handle, so the manager must keep every read within the limit.
        await using var source = CreateClientSource();
        var requestedNodeCounts = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var nextHandle = 0;
        var session = new Mock<ISession>();
        session.SetupGet(value => value.Connected).Returns(true);
        session.SetupGet(value => value.OperationLimits).Returns(new OperationLimits { MaxNodesPerRead = 1 });
        session
            .Setup(value => value.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ArrayOf<ReadValueId>>(),
                It.IsAny<CancellationToken>()))
            .Returns((RequestHeader requestHeader, double _, TimestampsToReturn _, ArrayOf<ReadValueId> nodesToRead, CancellationToken _) =>
            {
                requestedNodeCounts.Enqueue(nodesToRead.Count);
                var handle = (uint)Interlocked.Increment(ref nextHandle);
                requestHeader.RequestHandle = handle;
                return new ValueTask<ReadResponse>(new ReadResponse
                {
                    // Only the first request is answered for another request.
                    ResponseHeader = new ResponseHeader { RequestHandle = handle == 1 ? 99u : handle },
                    Results = Enumerable.Range(0, nodesToRead.Count)
                        .Select(_ => new DataValue(new Variant("polled"), StatusCodes.Good, DateTime.UtcNow))
                        .ToArray(),
                    DiagnosticInfos = []
                });
            });

        var configuration = CreateConfiguration();
        configuration.PollingInterval = TimeSpan.FromMilliseconds(10);
        configuration.PollingDisposalTimeout = TestTimeouts.Default;

        var registeredSubject = new RegisteredSubject((TestRoot)source.RootSubject);
        var metrics = new PollingMetrics();
        var reportedErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        await using var manager = new PollingManager(
            source,
            sessionProvider: () => session.Object,
            new SubjectPropertyWriter(source, NullLogger.Instance),
            configuration,
            metrics,
            reportedErrors.Enqueue,
            NullLogger.Instance);

        foreach (var (name, index) in new[] { (nameof(TestRoot.Name), 0), (nameof(TestRoot.Number_Unit), 1) })
        {
            manager.AddItem(new NodeId("Polled" + index, 2), registeredSubject.TryGetProperty(name)!);
        }

        // Act
        manager.Start();
        await AsyncTestHelpers.WaitUntilAsync(
            () => metrics.TotalReads >= 1 && metrics.FailedReads >= 1,
            message: "The poll should fail the mismatched batch and read the other one.",
            pollInterval: TimeSpan.FromMilliseconds(10));
        await manager.DisposeAsync();

        // Assert
        Assert.All(requestedNodeCounts, count => Assert.Equal(1, count));
        Assert.Equal(1, metrics.FailedReads);
        Assert.Collection(reportedErrors, error => Assert.IsType<OpcUaResponseMismatchException>(error));
    }
}
