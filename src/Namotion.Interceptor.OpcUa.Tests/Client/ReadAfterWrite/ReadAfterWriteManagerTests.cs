using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client.ReadAfterWrite;

/// <summary>
/// Tests for ReadAfterWriteManager - verifies behavior through observable outcomes (metrics).
/// </summary>
public class ReadAfterWriteManagerTests : IAsyncDisposable
{
    private readonly ReadAfterWriteManager _manager;
    private readonly ReadAfterWriteMetrics _metrics = new();
    private readonly TestPerson _testSubject;

    private static RegisteredSubjectProperty CreateTestProperty(TestPerson subject, string name = "FirstName")
    {
        var registeredSubject = new RegisteredSubject(subject);
        return registeredSubject.TryGetProperty(name)!;
    }

    private static OpcUaClientConfiguration CreateConfiguration(
        TimeSpan readAfterWriteBuffer,
        OpcUaValueConverter? valueConverter = null) =>
        new()
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = valueConverter ?? new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(Connectors.DefaultSubjectFactory.Instance),
            ReadAfterWriteBuffer = readAfterWriteBuffer
        };

    private static Mock<ISession> CreateSessionReturning(params DataValue[] results)
    {
        var session = new Mock<ISession>();
        session.SetupGet(value => value.Connected).Returns(true);
        session
            .Setup(value => value.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                ResponseHeader = new ResponseHeader(),
                Results = new DataValueCollection(results),
                DiagnosticInfos = []
            });

        return session;
    }

    private static DataValue CreateDataValue(object? value, StatusCode statusCode, DateTime sourceTimestamp) =>
        new()
        {
            Value = value,
            StatusCode = statusCode,
            SourceTimestamp = sourceTimestamp
        };

    private static void RegisterAndSchedule(
        ReadAfterWriteManager manager,
        NodeId nodeId,
        RegisteredSubjectProperty property,
        TimeSpan? revisedSamplingInterval = null)
    {
        manager.RegisterProperty(
            nodeId,
            property,
            requestedSamplingInterval: 0,
            revisedSamplingInterval: revisedSamplingInterval ?? TimeSpan.FromMinutes(1));
        manager.OnPropertyWritten(nodeId, sentRevision: 0);
    }

    public ReadAfterWriteManagerTests()
    {
        _testSubject = new TestPerson(InterceptorSubjectContext.Create());
        var configuration = CreateConfiguration(TimeSpan.FromMilliseconds(50));

        // Create manager with null session provider (for unit tests)
        _manager = new ReadAfterWriteManager(
            sessionProvider: () => null,
            source: null!, // Not used in these unit tests
            configuration,
            _metrics,
            reportError: static _ => { },
            NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
    }

    [Fact]
    public void InitialState_HasZeroMetrics()
    {
        Assert.Equal(0, _metrics.Scheduled);
        Assert.Equal(0, _metrics.Executed);
        Assert.Equal(0, _metrics.Coalesced);
        Assert.Equal(0, _metrics.Failed);
        Assert.Equal(0, _manager.PendingReadCount);
    }

    [Fact]
    public void RegisterProperty_WithSamplingIntervalZeroRevised_TracksForReadAfterWrites()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);

        // Act - requested 0 (exception-based), but server revised to 500ms
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);

        // Assert - should have scheduled a read-after-write
        Assert.Equal(1, _metrics.Scheduled);
        Assert.Equal(1, _manager.PendingReadCount);
    }

    [Fact]
    public void RegisterProperty_WithNonZeroSamplingInterval_DoesNotTrackForReadAfterWrites()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);

        // Act - requested 100ms (not exception-based), so not tracked
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 100, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);

        // Assert - should NOT have scheduled (sampling interval wasn't 0)
        Assert.Equal(0, _metrics.Scheduled);
    }

    [Fact]
    public void UnregisterProperty_PreventsSubsequentScheduling()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);
        Assert.Equal(1, _metrics.Scheduled);

        // Act
        _manager.UnregisterProperty(nodeId);
        _manager.OnPropertyWritten(nodeId, sentRevision: 1); // Should not schedule after unregister

        // Assert - still only 1 scheduled (second write ignored)
        Assert.Equal(1, _metrics.Scheduled);
    }

    [Fact]
    public void OnPropertyWritten_CoalescesMultipleWrites()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));

        // Act - write twice
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);

        // Assert - one scheduled, one coalesced
        Assert.Equal(1, _metrics.Scheduled);
        Assert.Equal(1, _metrics.Coalesced);
    }

    [Fact]
    public void ClearPendingReads_KeepsTrackedProperties()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);
        Assert.Equal(1, _metrics.Scheduled);

        // Act
        _manager.ClearPendingReads();
        Assert.Equal(0, _manager.PendingReadCount);

        // Write again - should still be able to schedule (property still tracked)
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);

        // Assert - second write should schedule
        Assert.Equal(2, _metrics.Scheduled);
        Assert.Equal(1, _manager.PendingReadCount);
    }

    [Fact]
    public void ClearAll_RemovesTrackedProperties()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);
        Assert.Equal(1, _metrics.Scheduled);

        // Act
        _manager.ClearAll();

        // Write again - should NOT schedule (property no longer tracked)
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);

        // Assert - still only 1 scheduled (property removed)
        Assert.Equal(1, _metrics.Scheduled);
    }

    [Fact]
    public async Task ConcurrentOperations_AreThreadSafe()
    {
        // Arrange
        const int operationsPerThread = 100;
        const int threadCount = 4;

        // Act - concurrent register/write/unregister operations should not throw
        var tasks = Enumerable.Range(0, threadCount)
            .Select(threadIndex => Task.Run(() =>
            {
                for (var i = 0; i < operationsPerThread; i++)
                {
                    var nodeId = new NodeId($"Node_{threadIndex}_{i}", 2);
                    _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(100));
                    _manager.OnPropertyWritten(nodeId, sentRevision: 1);
                    _manager.UnregisterProperty(nodeId);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - should have scheduled one per operation
        Assert.Equal(threadCount * operationsPerThread, _metrics.Scheduled);
        Assert.Equal(0, _manager.PendingReadCount);
    }

    [Fact]
    public async Task WhenGoodAndBadResultsAreMixed_ThenEachResultIsCountedByOutcome()
    {
        // Arrange
        var subjects = Enumerable.Range(0, 3)
            .Select(_ => new TestPerson(InterceptorSubjectContext.Create().WithFullPropertyTracking()))
            .ToArray();
        var timestamp = DateTime.UtcNow.AddMinutes(1);
        var session = CreateSessionReturning(
            CreateDataValue("first", StatusCodes.Good, timestamp),
            CreateDataValue("ignored", StatusCodes.BadUnexpectedError, timestamp),
            CreateDataValue("last", StatusCodes.Good, timestamp));
        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(250)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2),
            CreateTestProperty(subjects[0]));
        RegisterAndSchedule(manager, new NodeId("Scores", 2),
            CreateTestProperty(subjects[1]));
        RegisterAndSchedule(manager, new NodeId("LastName", 2),
            CreateTestProperty(subjects[2]));

        // Act
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert
        Assert.Equal(2, metrics.Executed);
        Assert.Equal(1, metrics.Failed);
        Assert.Equal([string.Empty, "first", "last"], subjects.Select(subject => subject.FirstName).Order());
        Assert.Equal(0, manager.PendingReadCount);
    }

    [Fact]
    public async Task WhenTheServerOmitsResults_ThenEachMissingResultIsCountedAsFailed()
    {
        // Arrange
        var subjects = Enumerable.Range(0, 3)
            .Select(_ => new TestPerson(InterceptorSubjectContext.Create().WithFullPropertyTracking()))
            .ToArray();
        var session = CreateSessionReturning(
            CreateDataValue("first", StatusCodes.Good, DateTime.UtcNow.AddMinutes(1)));
        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(250)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2),
            CreateTestProperty(subjects[0]));
        RegisterAndSchedule(manager, new NodeId("LastName", 2),
            CreateTestProperty(subjects[1]));
        RegisterAndSchedule(manager, new NodeId("Scores", 2),
            CreateTestProperty(subjects[2]));

        // Act
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert
        Assert.Equal(1, metrics.Executed);
        Assert.Equal(2, metrics.Failed);
        Assert.Equal(1, subjects.Count(subject => subject.FirstName == "first"));
    }

    [Fact]
    public async Task WhenTheSessionIsDownWhenReadsFallDue_ThenTheDrainedReadsAreCountedAsFailed()
    {
        // Arrange
        var session = new Mock<ISession>();
        session.SetupGet(value => value.Connected).Returns(false);
        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(250)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2), CreateTestProperty(_testSubject));
        RegisterAndSchedule(manager, new NodeId("LastName", 2), CreateTestProperty(_testSubject));

        // Act
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert
        Assert.Equal(2, metrics.Failed);
        Assert.Equal(0, metrics.Executed);
        Assert.Equal(0, manager.PendingReadCount);
        Assert.False(GetCircuitBreaker(manager).IsOpen);
        session.Verify(value => value.ReadAsync(
            It.IsAny<RequestHeader>(),
            It.IsAny<double>(),
            It.IsAny<TimestampsToReturn>(),
            It.IsAny<ReadValueIdCollection>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenOneConversionThrows_ThenTheOtherReadBacksStillApplyAndTheFailureIsReportedOnce()
    {
        // Arrange - the throwing value sits in the middle, so an uncontained throw would also
        // discard the read-back behind it
        var subjects = Enumerable.Range(0, 3)
            .Select(_ => new TestPerson(InterceptorSubjectContext.Create().WithFullPropertyTracking()))
            .ToArray();
        var timestamp = DateTime.UtcNow.AddMinutes(1);
        var session = CreateSessionReturning(
            CreateDataValue("first", StatusCodes.Good, timestamp),
            CreateDataValue("throw", StatusCodes.Good, timestamp),
            CreateDataValue("third", StatusCodes.Good, timestamp));
        var metrics = new ReadAfterWriteMetrics();
        var conversionError = new InvalidOperationException("conversion failed");
        var reportedErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(250), new ThrowingValueConverter("throw", conversionError)),
            metrics,
            reportedErrors.Enqueue,
            NullLogger.Instance);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2),
            CreateTestProperty(subjects[0]));
        RegisterAndSchedule(manager, new NodeId("Scores", 2),
            CreateTestProperty(subjects[1]));
        RegisterAndSchedule(manager, new NodeId("LastName", 2),
            CreateTestProperty(subjects[2]));

        // Act
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert - the contained failure counts as not applied, not as a read failure
        Assert.Equal(2, metrics.Executed);
        Assert.Equal(1, metrics.NotApplied);
        Assert.Equal(0, metrics.Failed);
        Assert.Equal(1, subjects.Count(subject => subject.FirstName == "first"));
        Assert.Equal(1, subjects.Count(subject => subject.FirstName == "third"));
        Assert.Equal(1, subjects.Count(subject => subject.FirstName == string.Empty));
        Assert.Collection(reportedErrors, error => Assert.Same(conversionError, error));
    }

    [Fact]
    public async Task WhenAGoodResultIsStale_ThenItIsNeitherExecutedNorFailed()
    {
        // Arrange
        var subject = new TestPerson(InterceptorSubjectContext.Create().WithFullPropertyTracking());
        var registeredSubject = new RegisteredSubject(subject);
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            subject.FirstName = "newer";
        }

        var session = CreateSessionReturning(
            CreateDataValue("stale", StatusCodes.Good, staleTimestamp.UtcDateTime),
            CreateDataValue("applied", StatusCodes.Good, DateTime.UtcNow.AddMinutes(1)));
        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(250)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2),
            registeredSubject.TryGetProperty(nameof(TestPerson.FirstName))!);
        RegisterAndSchedule(manager, new NodeId("LastName", 2),
            registeredSubject.TryGetProperty(nameof(TestPerson.LastName))!);

        // Act
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert
        Assert.Equal(1, metrics.Executed);
        Assert.Equal(0, metrics.Failed);
        Assert.Equal("newer", subject.FirstName);
        Assert.Equal("applied", subject.LastName);
    }

    [Fact]
    public async Task WhenAReadBackIsSkippedAsStale_ThenTheDiagnosticsReportIt()
    {
        // Arrange - a local write newer than the read-back's answer, so the answer is correctly discarded
        var subject = new TestPerson(InterceptorSubjectContext.Create().WithFullPropertyTracking());
        var registeredSubject = new RegisteredSubject(subject);
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            subject.FirstName = "newer";
        }

        var session = CreateSessionReturning(
            CreateDataValue("stale", StatusCodes.Good, staleTimestamp.UtcDateTime));
        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(250)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);
        var diagnostics = new ReadAfterWriteDiagnostics(manager, metrics);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2),
            registeredSubject.TryGetProperty(nameof(TestPerson.FirstName))!);

        // Act
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert - a skip is a read that succeeded, so it is neither executed, not applied nor failed
        Assert.Equal(1, diagnostics.TotalSkippedReads);
        Assert.Equal(0, diagnostics.TotalExecutedReads);
        Assert.Equal(0, diagnostics.TotalNotAppliedReads);
        Assert.Equal(0, diagnostics.TotalFailedReads);
    }

    [Fact]
    public async Task WhenAReadBackIsNotApplied_ThenTheDiagnosticsReportIt()
    {
        // Arrange - the server answers the read, but the value cannot be converted locally
        var subject = new TestPerson(InterceptorSubjectContext.Create().WithFullPropertyTracking());
        var session = CreateSessionReturning(
            CreateDataValue("throw", StatusCodes.Good, DateTime.UtcNow.AddMinutes(1)));
        var metrics = new ReadAfterWriteMetrics();
        var conversionError = new InvalidOperationException("conversion failed");
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(250), new ThrowingValueConverter("throw", conversionError)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);
        var diagnostics = new ReadAfterWriteDiagnostics(manager, metrics);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2), CreateTestProperty(subject));

        // Act
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert - unlike a skip this is a failure, but one contained to the node, not a failed read
        Assert.Equal(1, diagnostics.TotalNotAppliedReads);
        Assert.Equal(0, diagnostics.TotalExecutedReads);
        Assert.Equal(0, diagnostics.TotalSkippedReads);
        Assert.Equal(0, diagnostics.TotalFailedReads);
    }

    [Fact]
    public async Task WhenCompletionLoggingThrows_ThenTheReadOutcomeIsCountedOnce()
    {
        // Arrange
        var subjects = Enumerable.Range(0, 3)
            .Select(_ => new TestPerson(InterceptorSubjectContext.Create().WithFullPropertyTracking()))
            .ToArray();
        var timestamp = DateTime.UtcNow.AddMinutes(1);
        var session = CreateSessionReturning(
            CreateDataValue("first", StatusCodes.Good, timestamp),
            CreateDataValue("last", StatusCodes.Good, timestamp));
        var metrics = new ReadAfterWriteMetrics();
        var loggingError = new InvalidOperationException("logging failed");
        var logger = new ThrowingLogger(loggingError);
        var reportedErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var configuration = CreateConfiguration(TimeSpan.FromMinutes(1));
        configuration.PollingCircuitBreakerThreshold = 1;
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            configuration,
            metrics,
            reportedErrors.Enqueue,
            logger);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2), CreateTestProperty(subjects[0]));
        RegisterAndSchedule(manager, new NodeId("LastName", 2), CreateTestProperty(subjects[1]));
        logger.ThrowNextLog();

        // Act & Assert - logging failures are unexpected infrastructure failures and propagate to
        // the outer timer guard rather than being reclassified as read failures here.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ProcessDueReadsAsync(DateTime.MaxValue));
        Assert.Same(loggingError, thrown);
        Assert.Equal(2, metrics.Executed);
        Assert.Equal(0, metrics.Failed);
        Assert.Empty(reportedErrors);

        // The logger failure must not count against the read circuit. At threshold one, any accidental
        // RecordFailure call would leave this follow-up pending instead of executing it.
        RegisterAndSchedule(manager, new NodeId("FollowUp", 2), CreateTestProperty(subjects[2]));
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        Assert.Equal(3, metrics.Executed);
        Assert.Equal(0, metrics.Failed);
        Assert.Equal(0, manager.PendingReadCount);
        Assert.Empty(reportedErrors);
    }

    [Fact]
    public async Task WhenCompletionLoggingThrowsOnTheTimer_ThenTheOuterGuardReportsItOnce()
    {
        // Arrange
        var subject = new TestPerson(InterceptorSubjectContext.Create().WithFullPropertyTracking());
        var session = CreateSessionReturning(
            CreateDataValue("applied", StatusCodes.Good, DateTime.UtcNow.AddMinutes(1)));
        var metrics = new ReadAfterWriteMetrics();
        var loggingError = new InvalidOperationException("completion logging failed");
        var logger = new CompletionThrowingLogger(loggingError);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        void ReportError(Exception exception)
        {
            reportedErrors.Enqueue(exception);
            reported.TrySetResult();
        }

        var configuration = CreateConfiguration(TimeSpan.Zero);
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            configuration,
            metrics,
            ReportError,
            logger);
        var nodeId = new NodeId("FirstName", 2);
        manager.RegisterProperty(
            nodeId,
            CreateTestProperty(subject),
            requestedSamplingInterval: 0,
            revisedSamplingInterval: TimeSpan.FromMilliseconds(1));

        // Act
        manager.OnPropertyWritten(nodeId, sentRevision: 0);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await logger.WaitUntilOuterGuardLogsAsync();

        // Assert
        Assert.Collection(reportedErrors, error => Assert.Same(loggingError, error));
        Assert.Equal(1, metrics.Executed);
        Assert.Equal(0, metrics.Failed);
        Assert.Equal("applied", subject.FirstName);
    }

    [Fact]
    public async Task WhenPendingReadsMoveThroughTheirLifecycle_ThenTheCachedCountStaysCurrent()
    {
        // Arrange
        var firstNodeId = new NodeId("FirstName", 2);
        var lastNodeId = new NodeId("LastName", 2);
        _manager.RegisterProperty(firstNodeId, CreateTestProperty(_testSubject), 0, TimeSpan.FromMinutes(1));
        _manager.RegisterProperty(lastNodeId, CreateTestProperty(_testSubject, nameof(TestPerson.LastName)), 0, TimeSpan.FromMinutes(1));

        // Act & Assert
        _manager.OnPropertyWritten(firstNodeId, sentRevision: 0);
        Assert.Equal(1, _manager.PendingReadCount);

        _manager.OnPropertyWritten(firstNodeId, sentRevision: 0);
        Assert.Equal(1, _manager.PendingReadCount);

        _manager.OnPropertyWritten(lastNodeId, sentRevision: 0);
        Assert.Equal(2, _manager.PendingReadCount);

        _manager.UnregisterProperty(firstNodeId);
        Assert.Equal(1, _manager.PendingReadCount);

        _manager.ClearPendingReads();
        Assert.Equal(0, _manager.PendingReadCount);

        _manager.OnPropertyWritten(lastNodeId, sentRevision: 0);
        Assert.Equal(1, _manager.PendingReadCount);

        _manager.ClearAll();
        Assert.Equal(0, _manager.PendingReadCount);

        await _manager.DisposeAsync();
        Assert.Equal(0, _manager.PendingReadCount);
    }

    [Fact]
    public async Task WhenTheMutationLockIsHeld_ThenPendingCountCanStillBeRead()
    {
        // Arrange
        var logger = new BlockingLogger();
        await using var manager = new ReadAfterWriteManager(
            sessionProvider: () => null,
            source: null!,
            CreateConfiguration(TimeSpan.FromMinutes(1)),
            new ReadAfterWriteMetrics(),
            reportError: static _ => { },
            logger);
        var pendingNodeId = new NodeId("Pending", 2);
        manager.RegisterProperty(pendingNodeId, CreateTestProperty(_testSubject), 0, TimeSpan.FromMinutes(1));
        manager.OnPropertyWritten(pendingNodeId, sentRevision: 0);

        logger.BlockNextLog();
        var registrationTask = Task.Run(() => manager.RegisterProperty(
            new NodeId("Blocked", 2),
            CreateTestProperty(_testSubject),
            requestedSamplingInterval: 0,
            revisedSamplingInterval: TimeSpan.FromMinutes(1)));
        await logger.WaitUntilBlockedAsync();

        try
        {
            // Act
            var pendingCount = await Task.Run(() => manager.PendingReadCount).WaitAsync(TimeSpan.FromSeconds(1));

            // Assert
            Assert.Equal(1, pendingCount);
        }
        finally
        {
            logger.Release();
            await registrationTask;
        }
    }

    [Fact]
    public async Task DisposeAsync_CompletesGracefully()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);
        Assert.Equal(1, _metrics.Scheduled);

        // Act - Dispose should complete without throwing
        await _manager.DisposeAsync();

        // Assert - metrics remain stable after disposal
        Assert.Equal(1, _metrics.Scheduled);
    }

    [Fact]
    public async Task WhenOneReadBackValueCannotBeApplied_ThenTheRestOfTheBatchIsAppliedAndNoReadFailureIsRecorded()
    {
        // Arrange - two read-backs falling due in one batch, the first carrying a value its property
        // cannot hold. The read itself succeeds, so only the local apply fails.
        var registeredSubject = new RegisteredSubject(_testSubject);
        var failingProperty = registeredSubject.TryGetProperty(nameof(TestPerson.FirstName))!;
        var survivingProperty = registeredSubject.TryGetProperty(nameof(TestPerson.LastName))!;

        var failingNodeId = new NodeId("Failing", 2);
        var survivingNodeId = new NodeId("Surviving", 2);

        var session = new Mock<ISession>();
        session.SetupGet(s => s.Connected).Returns(true);
        session
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .Returns((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                var results = new DataValueCollection(nodesToRead.Count);
                foreach (var node in nodesToRead)
                {
                    results.Add(new DataValue
                    {
                        // An int cannot be stored in a string property, so applying it throws.
                        Value = node.NodeId == failingNodeId ? 42 : "from-server",
                        StatusCode = StatusCodes.Good,
                        SourceTimestamp = DateTime.UtcNow
                    });
                }

                return Task.FromResult(new ReadResponse
                {
                    ResponseHeader = new ResponseHeader(),
                    Results = results,
                    DiagnosticInfos = []
                });
            });

        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(200)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);

        manager.RegisterProperty(failingNodeId, failingProperty, requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(1));
        manager.RegisterProperty(survivingNodeId, survivingProperty, requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(1));

        // Act - the failing read-back is scheduled first, so an uncontained throw takes the other with it
        manager.OnPropertyWritten(failingNodeId, sentRevision: 0);
        manager.OnPropertyWritten(survivingNodeId, sentRevision: 0);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => _testSubject.LastName == "from-server",
            message: "the second read-back of the batch should have been applied");

        Assert.Equal(1, metrics.Executed);

        // The circuit breaker tracks how the server answers reads, and this read was answered
        Assert.Equal(0, metrics.Failed);
    }

    /// <summary>
    /// A connected session that answers every read with the same value and source timestamp.
    /// </summary>
    private static Mock<ISession> CreateSessionReturning(object? value, DateTime sourceTimestamp)
    {
        var session = new Mock<ISession>();
        session.SetupGet(s => s.Connected).Returns(true);
        session
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .Returns((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                var results = new DataValueCollection(nodesToRead.Count);
                for (var i = 0; i < nodesToRead.Count; i++)
                {
                    results.Add(new DataValue
                    {
                        Value = value,
                        StatusCode = StatusCodes.Good,
                        SourceTimestamp = sourceTimestamp
                    });
                }

                return Task.FromResult(new ReadResponse
                {
                    ResponseHeader = new ResponseHeader(),
                    Results = results,
                    DiagnosticInfos = []
                });
            });

        return session;
    }

    [Fact]
    public async Task WhenTheWrittenChangeCarriedNoRevisionAndALocalWriteFollowed_ThenTheReadBackIsSkipped()
    {
        // Arrange - a rollback and a transaction snapshot both reach a source as a change with no
        // revision, and nothing about them can be ranked by revision, so the write timestamps have to
        // separate the read-back from a local write that landed after it.
        var registeredSubject = new RegisteredSubject(_testSubject);
        var property = registeredSubject.TryGetProperty(nameof(TestPerson.FirstName))!;
        var nodeId = new NodeId("Revisionless", 2);
        var session = CreateSessionReturning("from-server", DateTime.UtcNow.AddMinutes(-1));

        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(200)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);

        manager.RegisterProperty(nodeId, property, requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(1));

        // Act - the local write commits after the read-back is scheduled, so it is newer than anything
        // the server's answer can carry.
        manager.OnPropertyWritten(nodeId, sentRevision: 0);
        _testSubject.FirstName = "newer-local";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => metrics.Executed + metrics.Skipped >= 1,
            message: "the read-back should have run");

        Assert.Equal(1, metrics.Skipped);
        Assert.Equal("newer-local", _testSubject.FirstName);
    }

    [Fact]
    public async Task WhenTheServerAnswersWithoutASourceTimestamp_ThenTheValueIsAppliedAndNoReadFailureIsRecorded()
    {
        // Arrange - an omitted SourceTimestamp arrives as DateTime.MinValue with an unspecified kind,
        // which converts to DateTimeOffset by way of the local time zone and underflows east of UTC.
        var registeredSubject = new RegisteredSubject(_testSubject);
        var property = registeredSubject.TryGetProperty(nameof(TestPerson.FirstName))!;
        var nodeId = new NodeId("Timestampless", 2);
        var session = CreateSessionReturning("from-server", DateTime.MinValue);

        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            new Mock<Connectors.ISubjectSource>().Object,
            CreateConfiguration(TimeSpan.FromMilliseconds(200)),
            metrics,
            reportError: static _ => { },
            NullLogger.Instance);

        manager.RegisterProperty(nodeId, property, requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(1));
        _testSubject.FirstName = "local";

        // Act - a revision above every local one, so nothing local outranks the write being verified and
        // the read-back is applied. Its timestamp is what the conversion has to survive producing.
        manager.OnPropertyWritten(nodeId, sentRevision: long.MaxValue);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => metrics.Executed + metrics.Skipped + metrics.Failed >= 1,
            message: "the read-back should have run");

        Assert.Equal("from-server", _testSubject.FirstName);

        // A conversion that throws locally would otherwise discard the batch and count against the
        // circuit breaker that tracks how the server answers reads.
        Assert.Equal(0, metrics.Failed);
    }

    [Fact]
    public async Task OnPropertyWritten_AfterDispose_IsIgnored()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));

        // Dispose the manager
        await _manager.DisposeAsync();

        var scheduledBefore = _metrics.Scheduled;

        // Act - Write after disposal should be ignored, not throw
        _manager.OnPropertyWritten(nodeId, sentRevision: 1);

        // Assert - metrics unchanged
        Assert.Equal(scheduledBefore, _metrics.Scheduled);
    }

    [Fact]
    public async Task WhenDisposalEndsAnSdkReadWithANonCancellationException_ThenItDoesNotRecordFailure()
    {
        // Arrange
        await using var source = ClientSourceTestFactory.CreateClientSource();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        async Task<ReadResponse> FailAfterCancellationAsync(CancellationToken cancellationToken)
        {
            readStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellation wait completed without cancellation.");
            }
            catch (OperationCanceledException)
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
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .Returns((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection _, CancellationToken cancellationToken) =>
                FailAfterCancellationAsync(cancellationToken));

        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.ReadAfterWriteBuffer = TimeSpan.FromMinutes(1);
        configuration.PollingCircuitBreakerThreshold = 1;
        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            () => session.Object,
            source,
            configuration,
            metrics,
            reportedErrors.Enqueue,
            NullLogger.Instance);
        RegisterAndSchedule(manager, new NodeId("FirstName", 2), CreateTestProperty(_testSubject));

        var processing = manager.ProcessDueReadsAsync(DateTime.MaxValue);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await manager.DisposeAsync();
        await processing;

        // Assert
        Assert.Equal((Executed: 0L, Failed: 0L), (metrics.Executed, metrics.Failed));
        Assert.False(GetCircuitBreaker(manager).IsOpen);
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenReadThrows_ThenSourceReportsFailureOnceAndKeepsItAfterRecovery()
    {
        // Arrange
        await using var source = ClientSourceTestFactory.CreateClientSource();
        var error = new InvalidOperationException("read-after-write failed");
        var reportCount = 0;

        void ReportError(Exception exception)
        {
            Interlocked.Increment(ref reportCount);
            source.ReportBackgroundError(exception);
        }

        var session = new Mock<ISession>();
        session.SetupGet(value => value.Connected).Returns(true);
        session
            .Setup(value => value.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);

        var configuration = ClientSourceTestFactory.CreateConfiguration();
        configuration.ReadAfterWriteBuffer = TimeSpan.FromMinutes(1);
        var metrics = new ReadAfterWriteMetrics();
        await using var manager = new ReadAfterWriteManager(
            sessionProvider: () => session.Object,
            source,
            configuration,
            metrics,
            ReportError,
            NullLogger.Instance);

        RegisterAndSchedule(manager, new NodeId("FirstName", 2),
            CreateTestProperty(_testSubject));
        RegisterAndSchedule(manager, new NodeId("LastName", 2),
            CreateTestProperty(_testSubject, nameof(TestPerson.LastName)));
        RegisterAndSchedule(manager, new NodeId("Scores", 2),
            CreateTestProperty(_testSubject, nameof(TestPerson.Scores)));

        // Act
        await manager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert
        Assert.Equal(1, Volatile.Read(ref reportCount));
        Assert.Equal(0, metrics.Executed);
        Assert.Equal(3, metrics.Failed);
        Assert.Same(error, source.Diagnostics.LastError);

        source.NotifySessionHealthy();
        Assert.Same(error, source.Diagnostics.LastError);
    }

    private static Connectors.Resilience.CircuitBreaker GetCircuitBreaker(ReadAfterWriteManager manager) =>
        (Connectors.Resilience.CircuitBreaker)typeof(ReadAfterWriteManager)
            .GetField("_circuitBreaker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!;

    private sealed class ThrowingValueConverter(object valueToThrow, Exception error) : OpcUaValueConverter
    {
        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
        {
            if (Equals(nodeValue, valueToThrow))
            {
                throw error;
            }

            return base.ConvertToPropertyValue(nodeValue, property);
        }
    }

    private sealed class BlockingLogger : ILogger
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private int _blockNextLog;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Interlocked.Exchange(ref _blockNextLog, 0) == 1)
            {
                _blocked.TrySetResult();
                _release.Wait();
            }
        }

        internal void BlockNextLog() => Volatile.Write(ref _blockNextLog, 1);

        internal Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        internal void Release() => _release.Set();
    }

    private sealed class ThrowingLogger(Exception error) : ILogger
    {
        private int _throwNextLog;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Interlocked.Exchange(ref _throwNextLog, 0) == 1)
            {
                throw error;
            }
        }

        internal void ThrowNextLog() => Volatile.Write(ref _throwNextLog, 1);
    }

    private sealed class CompletionThrowingLogger(Exception error) : ILogger
    {
        private readonly TaskCompletionSource _outerGuardLogged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completionLogThrown;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Debug &&
                message.StartsWith("Completed ", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _completionLogThrown, 1) == 0)
            {
                throw error;
            }

            if (logLevel == LogLevel.Error && ReferenceEquals(exception, error))
            {
                _outerGuardLogged.TrySetResult();
            }
        }

        internal Task WaitUntilOuterGuardLogsAsync() =>
            _outerGuardLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WhenReadIsOverdueAndMinimumDelayIsSet_ThenTimerWaitsForTheMinimumDelay()
    {
        // Arrange - the reads are already due, which is why the caller is rearming at all
        var utcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var earliestReadTime = utcNow - TimeSpan.FromSeconds(5);

        // Act
        var delay = ReadAfterWriteManager.CalculateTimerDelay(earliestReadTime, utcNow, TimeSpan.FromSeconds(30));

        // Assert - arming at zero would refire immediately and spin for the whole cooldown
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void WhenNothingIsPending_ThenTimerIsInfiniteRegardlessOfMinimumDelay()
    {
        // Arrange
        var utcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var delay = ReadAfterWriteManager.CalculateTimerDelay(DateTime.MaxValue, utcNow, TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(Timeout.InfiniteTimeSpan, delay);
    }

    [Fact]
    public void WhenReadIsFurtherOutThanMinimumDelay_ThenTimerWaitsForTheRead()
    {
        // Arrange
        var utcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var earliestReadTime = utcNow + TimeSpan.FromSeconds(45);

        // Act
        var delay = ReadAfterWriteManager.CalculateTimerDelay(earliestReadTime, utcNow, TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(45), delay);
    }

    [Fact]
    public void WhenReadIsOverdueAndMinimumDelayIsZero_ThenTimerFiresImmediately()
    {
        // Arrange
        var utcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var earliestReadTime = utcNow - TimeSpan.FromSeconds(5);

        // Act
        var delay = ReadAfterWriteManager.CalculateTimerDelay(earliestReadTime, utcNow, TimeSpan.Zero);

        // Assert
        Assert.Equal(TimeSpan.Zero, delay);
    }
}
