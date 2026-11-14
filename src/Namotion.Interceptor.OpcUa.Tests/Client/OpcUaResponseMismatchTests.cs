using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// A server can deliver the response to a different request. The client then fails the call without
/// applying anything from that response, and resynchronizes once.
/// </summary>
public class OpcUaResponseMismatchTests
{
    private const string NodeIdKey = "test-node-id";
    private const string FullStateSyncStarted = "Performing full state sync";

    [Fact]
    public async Task WhenWritesReceiveResponsesWithAnotherRequestHandle_ThenTheyFailAndOneFullStateSyncRuns()
    {
        // Arrange
        var logger = new RecordingLogger();
        var source = CreateClientSource(configuration: CreateConfigurationWithoutBackgroundReads());
        await using var sessionManager = CreateSessionManager(source, logger, new ManualTimeProvider());
        var session = CreateSessionAnsweringWritesWith(responseHandleOffset: 1);
        var writer = CreateWriter(session.Object, sessionManager.ScheduleFullStateSyncAfterResponseMismatch, logger);
        var changes = CreateChanges("first", "second");

        // Act
        var firstResult = await writer.WriteChangesAsync(changes, CancellationToken.None);
        var secondResult = await writer.WriteChangesAsync(changes, CancellationToken.None);
        await sessionManager.PerformFullStateSyncIfNeededAsync(CancellationToken.None);
        await sessionManager.PerformFullStateSyncIfNeededAsync(CancellationToken.None);

        // Assert
        foreach (var result in new[] { firstResult, secondResult })
        {
            Assert.IsType<OpcUaResponseMismatchException>(result.Error);
            Assert.Equal(changes.Length, result.FailedChanges.Length);
        }

        // One error per mismatch, and none of them carries a stack for a later log to repeat.
        Assert.Equal(2, logger.Errors.Count);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.Single(logger.Entries, entry => entry.Message.StartsWith(FullStateSyncStarted));
    }

    [Fact]
    public async Task WhenAWriteReceivesAResponseOfAnotherServiceType_ThenItFailsAndAFullStateSyncIsScheduled()
    {
        // Arrange
        var session = CreateConnectedSession();
        session
            .Setup(value => value.WriteAsync(It.IsAny<RequestHeader>(), It.IsAny<WriteValueCollection>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidCastException("Unable to cast object of type 'Opc.Ua.PublishResponse' to type 'Opc.Ua.WriteResponse'."));

        var scheduledSyncs = 0;
        var writer = CreateWriter(session.Object, () => scheduledSyncs++, NullLogger.Instance);
        var changes = CreateChanges("first");

        // Act
        var result = await writer.WriteChangesAsync(changes, CancellationToken.None);

        // Assert
        var error = Assert.IsType<OpcUaResponseMismatchException>(result.Error);
        Assert.Contains("PublishResponse", error.Message);
        Assert.Equal(changes.Length, result.FailedChanges.Length);
        Assert.Equal(1, scheduledSyncs);
    }

    [Fact]
    public async Task WhenAWriteReceivesTheResponseToItsOwnRequest_ThenItSucceedsWithoutScheduling()
    {
        // Arrange
        var session = CreateSessionAnsweringWritesWith(responseHandleOffset: 0);
        var scheduledSyncs = 0;
        var writer = CreateWriter(session.Object, () => scheduledSyncs++, NullLogger.Instance);

        // Act
        var result = await writer.WriteChangesAsync(CreateChanges("first"), CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        Assert.Equal(0, scheduledSyncs);
    }

    [Fact]
    public async Task WhenMismatchesKeepArriving_ThenFullStateSyncsRunAtMostOncePerInterval()
    {
        // Arrange
        var logger = new RecordingLogger();
        var timeProvider = new ManualTimeProvider();
        var source = CreateClientSource(configuration: CreateConfigurationWithoutBackgroundReads());
        await using var sessionManager = CreateSessionManager(source, logger, timeProvider);

        sessionManager.ScheduleFullStateSyncAfterResponseMismatch();
        await sessionManager.PerformFullStateSyncIfNeededAsync(CancellationToken.None);

        // Act
        sessionManager.ScheduleFullStateSyncAfterResponseMismatch();
        await sessionManager.PerformFullStateSyncIfNeededAsync(CancellationToken.None);
        var syncsWithinInterval = CountFullStateSyncs(logger);

        timeProvider.Advance(SessionManager.ResponseMismatchSyncInterval);
        await sessionManager.PerformFullStateSyncIfNeededAsync(CancellationToken.None);
        await sessionManager.PerformFullStateSyncIfNeededAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, syncsWithinInterval);
        Assert.Equal(2, CountFullStateSyncs(logger));
    }

    [Fact]
    public async Task WhenNoMismatchWasReported_ThenNoFullStateSyncRuns()
    {
        // Arrange
        var logger = new RecordingLogger();
        var source = CreateClientSource(configuration: CreateConfigurationWithoutBackgroundReads());
        await using var sessionManager = CreateSessionManager(source, logger, new ManualTimeProvider());

        // Act
        await sessionManager.PerformFullStateSyncIfNeededAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, CountFullStateSyncs(logger));
    }

    private static int CountFullStateSyncs(RecordingLogger logger) =>
        logger.Entries.Count(entry => entry.Message.StartsWith(FullStateSyncStarted));

    private static OpcUaClientConfiguration CreateConfigurationWithoutBackgroundReads()
    {
        var configuration = CreateConfiguration();
        configuration.EnablePollingFallback = false;
        configuration.EnableReadAfterWrite = false;
        return configuration;
    }

    private static SessionManager CreateSessionManager(OpcUaSubjectClientSource source, ILogger logger, TimeProvider timeProvider) =>
        new(
            source,
            new SubjectPropertyWriter(source, NullLogger.Instance),
            CreateConfigurationWithoutBackgroundReads(),
            source.PollingMetrics,
            source.ReadAfterWriteMetrics,
            logger,
            timeProvider);

    private static OutboundWriter CreateWriter(ISession session, Action onResponseMismatch, ILogger logger) =>
        new(
            () => session,
            readAfterWriteManager: null,
            onResponseMismatch,
            CreateConfiguration(),
            NodeIdKey,
            new ThroughputCounter(),
            logger);

    private static Mock<ISession> CreateConnectedSession()
    {
        var session = new Mock<ISession>();
        session.SetupGet(value => value.Connected).Returns(true);
        return session;
    }

    /// <summary>
    /// Assigns a request handle the way the SDK does while sending, and answers with that handle plus
    /// <paramref name="responseHandleOffset"/>.
    /// </summary>
    private static Mock<ISession> CreateSessionAnsweringWritesWith(uint responseHandleOffset)
    {
        var session = CreateConnectedSession();
        uint nextHandle = 0;
        session
            .Setup(value => value.WriteAsync(It.IsAny<RequestHeader>(), It.IsAny<WriteValueCollection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader requestHeader, WriteValueCollection nodesToWrite, CancellationToken _) =>
            {
                requestHeader.RequestHandle = ++nextHandle;
                return new WriteResponse
                {
                    ResponseHeader = new ResponseHeader { RequestHandle = requestHeader.RequestHandle + responseHandleOffset },
                    Results = new StatusCodeCollection(nodesToWrite.Select(_ => new StatusCode(StatusCodes.Good))),
                    DiagnosticInfos = []
                };
            });

        return session;
    }

    private static SubjectPropertyChange[] CreateChanges(params string[] values)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        return values
            .Select((value, index) =>
            {
                var subject = new TestPerson(context);
                var property = new PropertyReference(subject, nameof(TestPerson.FirstName));
                property.SetPropertyData(NodeIdKey, new NodeId("FirstName" + index, 2));
                return SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, string.Empty, value);
            })
            .ToArray();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
