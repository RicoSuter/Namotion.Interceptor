using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// The rule the server's inbound write path must hold: the node and the subject may never disagree about
/// what a client wrote. The SDK commits a write into the node before the connector gets to apply it, so
/// every way the apply can fail leaves the node serving a value the model refused, at Good quality, with
/// nothing to correct it afterwards.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaServerWriteIntegrityTests
{
    /// <summary>A value the validation interceptor refuses, on a property nothing else writes.</summary>
    private const double RefusedDouble = 42d;

    private readonly ITestOutputHelper _output;

    public OpcUaServerWriteIntegrityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WhenValidationRejectsAClientWrite_ThenTheNodeKeepsTheModelValueAndTheClientIsTold()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(
            _output, writeInterceptor: new ThrowOnValueInterceptor(WriteIntegrityChild.RejectedValue));

        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Value));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, WriteIntegrityChild.RejectedValue);

        // Assert
        var readBack = await fixture.Session.ReadAsync(nodeId);
        Assert.Equal(WriteIntegrityFixture.InitialValue, fixture.Child.Value);
        Assert.Equal(WriteIntegrityFixture.InitialValue, readBack.Value);
        Assert.True(StatusCode.IsBad(statusCode), $"A refused write must not be answered with '{statusCode}'.");
    }

    [Fact]
    public async Task WhenAnOnChangingHookCancelsAClientWrite_ThenTheNodeKeepsTheModelValueAndTheClientIsTold()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Vetoed));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, WriteIntegrityChild.VetoedValue);

        // Assert
        var readBack = await fixture.Session.ReadAsync(nodeId);
        Assert.Equal(WriteIntegrityFixture.InitialValue, fixture.Child.Vetoed);
        Assert.Equal(WriteIntegrityFixture.InitialValue, readBack.Value);
        Assert.True(StatusCode.IsBad(statusCode), $"A cancelled write must not be answered with '{statusCode}'.");
    }

    [Fact]
    public async Task WhenTheInboundConverterThrows_ThenTheRestOfTheWriteRequestStillCompletes()
    {
        // Arrange: the converter refuses one value, which is what a scaling or enum mapping converter does
        // when a client sends something outside its domain.
        await using var fixture = await WriteIntegrityFixture.StartAsync(
            _output, valueConverter: new ThrowOnSentinelConverter("poison"));

        var poisonNodeId = fixture.NodeId(nameof(WriteIntegrityChild.Value));
        var survivorNodeId = fixture.NodeId(nameof(WriteIntegrityChild.Other));

        // Act: both nodes travel in one Write request, the poison one first.
        var writeFault = await Record.ExceptionAsync(() => fixture.Session.WriteManyAsync(
            new WriteValue
            {
                NodeId = poisonNodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                Value = new DataValue { Value = "poison", StatusCode = StatusCodes.Good, SourceTimestamp = DateTime.UtcNow }
            },
            new WriteValue
            {
                NodeId = survivorNodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                Value = new DataValue { Value = "survivor", StatusCode = StatusCodes.Good, SourceTimestamp = DateTime.UtcNow }
            }));

        // Assert: the sibling is the evidence. A conversion that cannot run is one node's problem, so the
        // rest of the request must still be served.
        _output.WriteLine($"Write request answered with: {writeFault?.ToString() ?? "no fault"}");
        Assert.Equal("survivor", fixture.Child.Other);
        Assert.Equal("survivor", fixture.Node(nameof(WriteIntegrityChild.Other)).Value);
    }

    [Fact]
    public async Task WhenTheConverterPairDoesNotRoundTrip_ThenTheNodeHoldsTheConvertedModelValueAndTheClientReceivesGood()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(
            _output, valueConverter: new ClampingValueConverter());

        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.ClampedValue));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, 500d);

        // Assert: the model took the clamped value, so that is what the node must serve. Anything else is
        // a read-back confirming a value the model never holds.
        var readBack = await fixture.Session.ReadAsync(nodeId);
        Assert.Equal(ClampingValueConverter.Maximum, fixture.Child.ClampedValue);
        Assert.Equal(ClampingValueConverter.Maximum, readBack.Value);
        Assert.True(StatusCode.IsGood(statusCode), $"An accepted write must not be answered with '{statusCode}'.");
    }

    [Fact]
    public async Task WhenTheOutboundConverterThrows_ThenTheClientReceivesGoodAndTheNodeReportsUncertain()
    {
        // Arrange: the model takes the value, but nothing can put what it now holds onto the node.
        await using var fixture = await WriteIntegrityFixture.StartAsync(
            _output, valueConverter: new ThrowOnOutboundSentinelConverter("poison"));

        var node = fixture.Node(nameof(WriteIntegrityChild.Value));
        var timestampBeforeTheWrite = node.Timestamp;

        // A timestamp no accepted write could plausibly carry, so the assertion cannot pass by coincidence.
        var clientTimestamp = DateTime.UtcNow.AddDays(-1);

        // Act
        var statusCode = await fixture.Session.WriteAsync(node.NodeId, "poison", sourceTimestamp: clientTimestamp);

        // Assert: the single path where the node does not end the write holding what the model holds. The
        // write was accepted, so the client is told so, and the node serves the last value this server
        // could represent rather than the client's, dated as it was, with the status code carrying the
        // caveat.
        var readBack = await fixture.Session.ReadAsync(node.NodeId);
        Assert.Equal("poison", fixture.Child.Value);
        Assert.True(StatusCode.IsGood(statusCode), $"An accepted write must not be answered with '{statusCode}'.");
        Assert.Equal(WriteIntegrityFixture.InitialValue, readBack.Value);
        Assert.Equal((StatusCode)StatusCodes.UncertainLastUsableValue, readBack.StatusCode);
        Assert.Equal(timestampBeforeTheWrite, readBack.SourceTimestamp);
    }

    [Fact]
    public async Task WhenTheModelStoresAnAcceptedArrayInItsOwnInstance_ThenTheClientReceivesGood()
    {
        // Arrange: a hook that hands back a copy, which is what a normalising hook or a copying write
        // interceptor does to every array that passes through it.
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.CopiedNumbers));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, new[] { 7, 8, 9 });

        // Assert: the model holds what the client asked for, so the write was taken. Answering on instance
        // identity would refuse every array write such a property ever accepts.
        Assert.Equal(new[] { 7, 8, 9 }, fixture.Child.CopiedNumbers);
        Assert.True(StatusCode.IsGood(statusCode), $"An accepted write must not be answered with '{statusCode}'.");
    }

    [Fact]
    public async Task WhenAClientWritesAByteStringIndexRange_ThenTheSubjectsPreviousInnerArrayIsNotMutated()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Blobs));

        var innerArrayBeforeTheWrite = fixture.Child.Blobs[0];
        var contentsBeforeTheWrite = innerArrayBeforeTheWrite.ToArray();

        // Act: bytes 1 and 2 of the first byte string, which the merge rewrites inside that inner array
        // rather than by replacing the element.
        var statusCode = await fixture.Session.WriteAsync(
            nodeId, new byte[][] { [0xAA, 0xBB] }, indexRange: "0,1:2");

        // Assert: copying the outer array alone is not enough. The subject's own inner arrays are values
        // like any other, and rewriting their bytes publishes nothing to anyone holding them.
        Assert.True(StatusCode.IsGood(statusCode), $"The index range write should be accepted, got '{statusCode}'.");
        Assert.Equal(contentsBeforeTheWrite, innerArrayBeforeTheWrite);
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.Child.Blobs[0].SequenceEqual(new byte[] { 1, 0xAA, 0xBB, 4 }),
            message: $"the subject should hold the merged byte string, holds [{string.Join(", ", fixture.Child.Blobs[0])}]");
    }

    [Fact]
    public async Task WhenAnIndexRangeWriteIsRejected_ThenTheNodeKeepsTheModelsArrayWithNoPendingChange()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var node = fixture.Node(nameof(WriteIntegrityChild.Numbers));

        // Act: past the end of a five element array, which the merge rejects after the copy was taken.
        var statusCode = await fixture.Session.WriteAsync(node.NodeId, new[] { 20, 30 }, indexRange: "10:11");

        // Assert: the copy exists only to be merged into, so a merge that never happened must leave no
        // trace. A node left holding it would serve an array the subject does not have, and the change
        // mask the assignment set would have a later flush publish a change nobody made.
        Assert.True(StatusCode.IsBad(statusCode), $"A rejected index range write must not be answered with '{statusCode}'.");
        Assert.Same(fixture.Child.Numbers, node.Value);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, fixture.Child.Numbers);
        Assert.Equal(NodeStateChangeMasks.None, node.ChangeMasks);
    }

    [Fact]
    public async Task WhenAnEnumPropertyIsWritten_ThenTheClientReceivesGood()
    {
        // Arrange: an enum reaches the property setter as a boxed int, so a path that reports the apply's
        // outcome must coerce it rather than let the unboxing cast decide the client's status code.
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Mode));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, (int)WriteIntegrityMode.Running);

        // Assert
        Assert.True(StatusCode.IsGood(statusCode), $"An enum write must not be answered with '{statusCode}'.");
    }

    /// <remarks>
    /// Green today, but not through the branch it names: the detach deletes the node before it unregisters
    /// the property, so the write is refused with BadNodeIdUnknown and never reaches the apply. The two
    /// steps cannot interleave the other way round, because the deletion runs inside the detach notification
    /// under the same node manager lock the SDK's write service takes, and the registry only drops the
    /// subject after that notification returns. So a node serving an unregistered property is not reachable
    /// through the model, and the silent return the apply takes for one stays unreachable with it. Kept as
    /// the guard that a write to a detached property is refused rather than applied somewhere.
    /// </remarks>
    [Fact]
    public async Task WhenThePropertyIsNotRegistered_ThenTheWriteIsRefusedBeforeTheNodeIsTouched()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);

        var child = fixture.Child;
        var property = fixture.Property(nameof(WriteIntegrityChild.Value));
        var node = fixture.Node(nameof(WriteIntegrityChild.Value));
        var nodeId = node.NodeId;
        var valueBeforeTheWrite = node.Value;

        fixture.Root.Child = null;

        await AsyncTestHelpers.WaitUntilAsync(
            () => property.TryGetRegisteredProperty() is null,
            message: "the detached child's property should have left the registry");

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, "written-while-unregistered");

        // Assert: nothing can carry this value into the model, so the node must not take it either.
        _output.WriteLine($"Write to the unregistered property answered with: {statusCode}");
        _output.WriteLine($"Child is still reachable from the test: {child.Value}");
        Assert.Equal(valueBeforeTheWrite, node.Value);
        Assert.True(StatusCode.IsBad(statusCode), $"An unappliable write must not be answered with '{statusCode}'.");
    }

    [Fact]
    public async Task WhenAClientWritesAnIndexRange_ThenTheSubjectsPreviousArrayIsNotMutated()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Numbers));

        var arrayBeforeTheWrite = fixture.Child.Numbers;
        var contentsBeforeTheWrite = arrayBeforeTheWrite.ToArray();

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, new[] { 20, 30 }, indexRange: "1:2");

        // Assert: the value the subject held when the write started is a value like any other. Nothing may
        // rewrite its elements in place, because every reader holding it would see the change without any
        // write having been published.
        Assert.True(StatusCode.IsGood(statusCode), $"The index range write should be accepted, got '{statusCode}'.");
        Assert.Equal(contentsBeforeTheWrite, arrayBeforeTheWrite);
    }

    [Fact]
    public async Task WhenAClientWritesAnIndexRange_ThenAChangeIsPublishedThroughTheInterceptorChain()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Numbers));
        var property = fixture.Property(nameof(WriteIntegrityChild.Numbers));

        var changes = new List<SubjectPropertyChange>();
        using var subscription = fixture.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => PropertyReference.Comparer.Equals(change.Property, property))
            .Subscribe(changes.Add);

        // A whole-array write proves the subscription is live and that this property does publish, so a
        // later empty list is evidence about the index range path rather than about the observer.
        await fixture.Session.WriteAsync(nodeId, new[] { 9, 9, 9, 9, 9 });
        Assert.NotEmpty(changes);
        changes.Clear();

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, new[] { 20, 30 }, indexRange: "1:2");

        // Assert: a partial write is still a write. Nothing downstream, no other connector, no derived
        // property and no observer, learns of it unless it goes through the chain.
        Assert.True(StatusCode.IsGood(statusCode), $"The index range write should be accepted, got '{statusCode}'.");
        Assert.NotEmpty(changes);
    }

    [Fact]
    public async Task WhenAWriteIsRefused_ThenTheNodeTimestampReflectsTheModel()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(
            _output, writeInterceptor: new ThrowOnValueInterceptor(WriteIntegrityChild.RejectedValue));

        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Value));
        var modelTimestamp = fixture.Property(nameof(WriteIntegrityChild.Value)).TryGetWriteTimestamp();
        Assert.NotNull(modelTimestamp);

        // A timestamp no accepted write could plausibly carry, so the assertion cannot pass by coincidence.
        var clientTimestamp = DateTime.UtcNow.AddDays(-1);

        // Act
        await fixture.Session.WriteAsync(nodeId, WriteIntegrityChild.RejectedValue, sourceTimestamp: clientTimestamp);

        // Assert: the node still serves the model's value, so it must serve the model's timestamp with it.
        // A refused write that leaves the client's timestamp behind dates a value that never changed.
        var readBack = await fixture.Session.ReadAsync(nodeId);
        Assert.NotEqual(clientTimestamp, readBack.SourceTimestamp);
        Assert.Equal(modelTimestamp!.Value.UtcDateTime, readBack.SourceTimestamp);
    }

    [Fact]
    public async Task WhenARefusedWriteTargetsANeverWrittenProperty_ThenTheNodeKeepsItsOwnTimestamp()
    {
        // Arrange: nothing seeds ClampedValue, so the model has no write timestamp for the node to fall
        // back to, which is the case the model's own timestamp cannot cover.
        await using var fixture = await WriteIntegrityFixture.StartAsync(
            _output, writeInterceptor: new ThrowOnValueInterceptor(RefusedDouble));

        var node = fixture.Node(nameof(WriteIntegrityChild.ClampedValue));
        Assert.Null(fixture.Property(nameof(WriteIntegrityChild.ClampedValue)).TryGetWriteTimestamp());
        var timestampBeforeTheWrite = node.Timestamp;

        // A timestamp no accepted write could plausibly carry, so the assertion cannot pass by coincidence.
        var clientTimestamp = DateTime.UtcNow.AddDays(-1);

        // Act
        var statusCode = await fixture.Session.WriteAsync(node.NodeId, RefusedDouble, sourceTimestamp: clientTimestamp);

        // Assert: the node's timestamp dates the value the node holds. A refused write leaves the model's
        // value there, so dating it with the client's timestamp claims a change that never happened.
        Assert.True(StatusCode.IsBad(statusCode), $"A refused write must not be answered with '{statusCode}'.");
        var readBack = await fixture.Session.ReadAsync(node.NodeId);
        Assert.NotEqual(clientTimestamp, readBack.SourceTimestamp);
        Assert.Equal(timestampBeforeTheWrite, readBack.SourceTimestamp);
    }

    [Fact]
    public async Task WhenAClientWritesTheWrongType_ThenTheServerAnswersBadTypeMismatch()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.ClampedValue));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, "not a double");

        // Assert: the type check runs before anything is committed, and must keep doing so.
        Assert.Equal((StatusCode)StatusCodes.BadTypeMismatch, statusCode);
        Assert.Equal(0d, fixture.Child.ClampedValue);
    }

    [Fact]
    public async Task WhenAClientWritesAnIndexRange_ThenTheSubjectHoldsTheMergedArray()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Numbers));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, new[] { 20, 30 }, indexRange: "1:2");

        // Assert: a partial write means the merged whole reaches the subject, not just the written elements.
        Assert.True(StatusCode.IsGood(statusCode), $"The index range write should be accepted, got '{statusCode}'.");
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.Child.Numbers.SequenceEqual([1, 20, 30, 4, 5]),
            message: $"the subject should hold the merged array, holds [{string.Join(", ", fixture.Child.Numbers)}]");
    }

    [Fact]
    public async Task WhenAClientOmitsTheSourceTimestamp_ThenTheWriteSucceeds()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Value));

        // Act: an unset source timestamp is what the SDK reads as not supplied and fills in itself.
        var statusCode = await fixture.Session.WriteAsync(nodeId, "undated", sourceTimestamp: DateTime.MinValue);

        // Assert
        Assert.True(StatusCode.IsGood(statusCode), $"An undated write must not be answered with '{statusCode}'.");
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.Child.Value == "undated",
            message: "the undated write should still reach the subject");
    }

    [Fact]
    public async Task WhenAWriteIsAccepted_ThenBothStoresHoldIt()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Value));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, "accepted");

        // Assert
        Assert.True(StatusCode.IsGood(statusCode), $"An accepted write must not be answered with '{statusCode}'.");
        Assert.Equal("accepted", fixture.Child.Value);

        var readBack = await fixture.Session.ReadAsync(nodeId);
        Assert.Equal("accepted", readBack.Value);
    }
}
