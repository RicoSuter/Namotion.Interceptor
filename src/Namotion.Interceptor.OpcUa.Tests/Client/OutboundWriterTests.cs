using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// How a failed write batch is reported, which is what decides whether the batches behind it in the
/// same flush are attempted at all: an enumerated failure names the changes the source refused and lets
/// the flush continue, an empty one says the call itself never answered and stops the flush.
/// </summary>
public class OutboundWriterTests
{
    private const string NodeIdKey = "OpcUaNodeId:test";

    [Fact]
    public async Task WhenTheWriteCallFaultsOnTheBatchContent_ThenTheBatchesChangesAreEnumerated()
    {
        // Arrange: the client stack raises BadRequestTooLarge while encoding the request, so the same
        // batch faults the same way on every retry. Stopping the flush there would starve the batches
        // behind it for good.
        var (writer, change) = CreateWriter(session => session
            .Setup(s => s.WriteAsync(It.IsAny<RequestHeader>(), It.IsAny<WriteValueCollection>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceResultException(StatusCodes.BadRequestTooLarge)));

        // Act
        var result = await writer.WriteChangesAsync(new[] { change }, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Equal(change.Property, Assert.Single(result.FailedChanges).Property);
    }

    [Fact]
    public async Task WhenTheValueConverterThrows_ThenTheBatchesChangesAreEnumerated()
    {
        // Arrange: conversion runs before anything is sent, so its throw says nothing about the session.
        var (writer, change) = CreateWriter(
            configureSession: _ => { },
            valueConverter: new ThrowOnOutboundSentinelConverter("new"));

        // Act
        var result = await writer.WriteChangesAsync(new[] { change }, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Equal(change.Property, Assert.Single(result.FailedChanges).Property);
    }

    [Fact]
    public async Task WhenTheWriteCallTimesOut_ThenNoChangesAreEnumerated()
    {
        // Arrange
        var (writer, change) = CreateWriter(session => session
            .Setup(s => s.WriteAsync(It.IsAny<RequestHeader>(), It.IsAny<WriteValueCollection>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceResultException(StatusCodes.BadTimeout)));

        // Act
        var result = await writer.WriteChangesAsync(new[] { change }, CancellationToken.None);

        // Assert: the session did not answer, so every remaining batch of the flush would cost another
        // operation timeout and naming no change is what stops it.
        Assert.NotNull(result.Error);
        Assert.Empty(result.FailedChanges);
    }

    [Fact]
    public async Task WhenTheServerAnswersWithFewerResultsThanNodes_ThenTheBatchesChangesAreEnumerated()
    {
        // Arrange: the service call only validates the response header, and the SDK's own count check
        // runs on a batched path this client never takes, so an under-length answer arrives unchecked.
        var (writer, change) = CreateWriter(session => session
            .Setup(s => s.WriteAsync(It.IsAny<RequestHeader>(), It.IsAny<WriteValueCollection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WriteResponse
            {
                ResponseHeader = new ResponseHeader(),
                Results = [],
                DiagnosticInfos = []
            }));

        // Act
        var result = await writer.WriteChangesAsync(new[] { change }, CancellationToken.None);

        // Assert: an unanswered node must not be confirmed written, which would drop it from the retry
        // queue for good and arm a read-back that reverts it. What this batch asks for decides the short
        // answer, so the retry queue re-forms it and it comes back short every time; naming no change
        // would stop the flush on every attempt and starve the batches behind it for good.
        Assert.NotNull(result.Error);
        Assert.Equal(change.Property, Assert.Single(result.FailedChanges).Property);
    }

    [Fact]
    public async Task WhenTheSessionIsNotConnected_ThenNoChangesAreEnumerated()
    {
        // Arrange
        var (writer, change) = CreateWriter(session => session.SetupGet(s => s.Connected).Returns(false));

        // Act
        var result = await writer.WriteChangesAsync(new[] { change }, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Empty(result.FailedChanges);
    }

    [Fact]
    public async Task WhenAMappedPropertyHasNoSetter_ThenNoReadBackIsScheduledForIt()
    {
        // Arrange: a batch of two mapped properties, only one of which the client can write. The request
        // therefore carries one node and the server answers one result.
        await using var manager = CreateReadAfterWriteManager();
        var (writer, writableChange) = CreateWriter(
            AnswerWith(StatusCodes.Good),
            readAfterWriteManager: manager);

        var setterlessChange = MapSetterlessProperty(writableChange);

        TrackForReadBack(manager, writableChange);
        TrackForReadBack(manager, setterlessChange);

        // Act
        var result = await writer.WriteChangesAsync(new[] { writableChange, setterlessChange }, CancellationToken.None);

        // Assert: nothing was written for the setter-less property, so a read-back for it would apply the
        // server's value over a local one that no write is going to replace.
        Assert.True(result.IsFullySuccessful);
        Assert.Equal(1, manager.PendingReadCount);
    }

    [Theory]
    [InlineData(StatusCodes.GoodCompletesAsynchronously)]
    // The low 16 bits describe the answer rather than name it, so a server free to set one must not be
    // able to turn this into a code the check does not recognise.
    [InlineData(StatusCodes.GoodCompletesAsynchronously | 0x0403u)]
    public async Task WhenTheServerWillCompleteTheWriteAsynchronously_ThenNoReadBackIsScheduled(uint statusCode)
    {
        // Arrange: GoodCompletesAsynchronously is a legal Write result for a gateway queueing writes down
        // to a device, and it is good, so the change is confirmed and leaves the retry queue.
        await using var manager = CreateReadAfterWriteManager();
        var (writer, change) = CreateWriter(
            AnswerWith(statusCode),
            readAfterWriteManager: manager);

        TrackForReadBack(manager, change);

        // Act
        var result = await writer.WriteChangesAsync(new[] { change }, CancellationToken.None);

        // Assert: the server has said the write is not done, so a read-back firing before it lands would
        // apply the pre-write value, and nothing redelivers the change.
        Assert.True(result.IsFullySuccessful);
        Assert.Equal(0, manager.PendingReadCount);
    }

    [Fact]
    public async Task WhenWriteSourceTimestampIsNotEnabled_ThenTheWriteCarriesNoSourceTimestamp()
    {
        // Arrange: Part 4 lets a server refuse any value, status and timestamp combination it does not
        // support with BadWriteNotSupported and perform no write at all, and the OPC Foundation's own
        // reference client never sets one. Against such a server every write would fail permanently.
        var sent = new List<WriteValueCollection>();
        var (writer, change) = CreateWriter(CaptureAndAnswerGood(sent));

        // Act
        await writer.WriteChangesAsync(new[] { change }, CancellationToken.None);

        // Assert
        var writeValue = Assert.Single(Assert.Single(sent));
        Assert.Equal(DateTime.MinValue, writeValue.Value.SourceTimestamp);
    }

    [Fact]
    public async Task WhenWriteSourceTimestampIsEnabled_ThenTheWriteCarriesTheChangeTimestamp()
    {
        // Arrange: the opt-in exists for a Namotion client and server pair, where the server stores the
        // timestamp it is given instead of stamping its own receive time.
        var sent = new List<WriteValueCollection>();
        var (writer, change) = CreateWriter(
            CaptureAndAnswerGood(sent),
            configure: configuration => configuration.WriteSourceTimestamp = true);

        // Act
        await writer.WriteChangesAsync(new[] { change }, CancellationToken.None);

        // Assert
        var writeValue = Assert.Single(Assert.Single(sent));
        Assert.Equal(change.ChangedTimestamp.UtcDateTime, writeValue.Value.SourceTimestamp);
    }

    /// <summary>
    /// Records every request and answers each of its nodes with Good.
    /// </summary>
    private static Action<Mock<ISession>> CaptureAndAnswerGood(List<WriteValueCollection> sent)
    {
        return session => session
            .Setup(s => s.WriteAsync(It.IsAny<RequestHeader>(), It.IsAny<WriteValueCollection>(), It.IsAny<CancellationToken>()))
            .Returns((RequestHeader _, WriteValueCollection nodesToWrite, CancellationToken _) =>
            {
                sent.Add(nodesToWrite);

                var results = new StatusCodeCollection(nodesToWrite.Count);
                for (var i = 0; i < nodesToWrite.Count; i++)
                {
                    results.Add(StatusCodes.Good);
                }

                return Task.FromResult(new WriteResponse
                {
                    ResponseHeader = new ResponseHeader(),
                    Results = results,
                    DiagnosticInfos = []
                });
            });
    }

    /// <summary>
    /// A manager that tracks every registered node but never reads: nothing provides a session and the
    /// revised intervals are far out, so a scheduled read-back stays pending for the test to count.
    /// </summary>
    private static ReadAfterWriteManager CreateReadAfterWriteManager()
    {
        return new ReadAfterWriteManager(
            sessionProvider: () => null,
            source: null!,
            new OpcUaClientConfiguration
            {
                ServerUrl = "opc.tcp://localhost:4840",
                TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
                ValueConverter = new OpcUaValueConverter(),
                SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
            },
            new ReadAfterWriteMetrics(),
            reportError: static _ => { },
            NullLogger.Instance);
    }

    /// <summary>
    /// Answers every write with one result per requested node, all carrying <paramref name="statusCode"/>.
    /// </summary>
    private static Action<Mock<ISession>> AnswerWith(uint statusCode)
    {
        return session => session
            .Setup(s => s.WriteAsync(It.IsAny<RequestHeader>(), It.IsAny<WriteValueCollection>(), It.IsAny<CancellationToken>()))
            .Returns((RequestHeader _, WriteValueCollection nodesToWrite, CancellationToken _) =>
            {
                var results = new StatusCodeCollection(nodesToWrite.Count);
                for (var i = 0; i < nodesToWrite.Count; i++)
                {
                    results.Add(statusCode);
                }

                return Task.FromResult(new WriteResponse
                {
                    ResponseHeader = new ResponseHeader(),
                    Results = results,
                    DiagnosticInfos = []
                });
            });
    }

    /// <summary>
    /// Maps a derived, setter-less property of the same subject to its own node and returns a change
    /// for it.
    /// </summary>
    private static SubjectPropertyChange MapSetterlessProperty(SubjectPropertyChange writableChange)
    {
        var property = new PropertyReference(writableChange.Property.Subject, nameof(TestPerson.FullName));
        property.SetPropertyData(NodeIdKey, new NodeId("Derived", 2));

        return SubjectPropertyChange.Create(
            property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "old", "new");
    }

    /// <summary>
    /// Tracks the node a change is mapped to, which is what makes a read-back scheduled for it countable.
    /// </summary>
    private static void TrackForReadBack(ReadAfterWriteManager manager, SubjectPropertyChange change)
    {
        Assert.True(change.Property.TryGetPropertyData(NodeIdKey, out var nodeId));

        manager.RegisterProperty(
            (NodeId)nodeId!,
            change.Property.TryGetRegisteredProperty()!,
            requestedSamplingInterval: 0,
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Builds a writer over a connected mocked session and one change mapped to a writable node.
    /// </summary>
    private static (OutboundWriter Writer, SubjectPropertyChange Change) CreateWriter(
        Action<Mock<ISession>> configureSession,
        OpcUaValueConverter? valueConverter = null,
        ReadAfterWriteManager? readAfterWriteManager = null,
        Action<OpcUaClientConfiguration>? configure = null)
    {
        var session = new Mock<ISession>();
        session.SetupGet(s => s.Connected).Returns(true);
        configureSession(session);

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle();

        var subject = new TestPerson(context);
        var property = new PropertyReference(subject, nameof(TestPerson.FirstName));
        property.SetPropertyData(NodeIdKey, new NodeId("Node", 2));

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = valueConverter ?? new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        };

        configure?.Invoke(configuration);

        var writer = new OutboundWriter(
            () => session.Object,
            readAfterWriteManager,
            configuration,
            NodeIdKey,
            new ThroughputCounter(),
            NullLogger.Instance);

        var change = SubjectPropertyChange.Create(
            property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "old", "new");

        return (writer, change);
    }
}
