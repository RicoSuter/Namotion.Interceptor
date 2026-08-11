using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
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
    public async Task WhenTheServerAnswersWithFewerResultsThanNodes_ThenNoChangesAreEnumerated()
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
        // queue for good and arm a read-back that reverts it.
        Assert.NotNull(result.Error);
        Assert.Empty(result.FailedChanges);
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

    /// <summary>
    /// Builds a writer over a connected mocked session and one change mapped to a writable node.
    /// </summary>
    private static (OutboundWriter Writer, SubjectPropertyChange Change) CreateWriter(
        Action<Mock<ISession>> configureSession,
        OpcUaValueConverter? valueConverter = null)
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

        var writer = new OutboundWriter(
            () => session.Object,
            readAfterWriteManager: null,
            configuration,
            NodeIdKey,
            new ThroughputCounter(),
            NullLogger.Instance);

        var change = SubjectPropertyChange.Create(
            property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "old", "new");

        return (writer, change);
    }
}
