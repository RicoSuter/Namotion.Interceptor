using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// What one unconvertible change costs the changes travelling with it, on both sides: which batches of a
/// multi-batch client flush are still attempted, and which properties of one server batch still reach
/// their nodes.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaOutboundWriteTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaOutboundWriteTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WhenAChangesConversionThrows_ThenTheBatchBehindItIsStillWritten()
    {
        // Arrange: one node per write request, so the two properties leave in two batches.
        await using var fixture = await InboundStatusFixture.StartAsync(
            _output, valueConverter: new ThrowOnOutboundSentinelConverter("poison"));
        fixture.LimitWritesToOneNodePerRequest();

        // Act: one commit hands both changes to the source as a single flush, the poisoned one first.
        // Conversion runs before anything is sent, so its throw says nothing about the session and must
        // not condemn the batch behind it.
        using var transaction = await fixture.ClientContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        fixture.ClientRoot.Child!.Value = "poison";
        fixture.ClientRoot.Child!.Other = "outbound-survivor";
        await Assert.ThrowsAsync<SubjectTransactionException>(
            async () => await transaction.CommitAsync(CancellationToken.None));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ServerRoot.Child?.Other == "outbound-survivor",
            message: "the batch behind the one that could not be converted should still have been written");
    }

    [Fact]
    public async Task WhenAChangesConversionThrows_ThenItsNodeReportsUncertainAndIsFlushed()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(
            _output, valueConverter: new ThrowOnOutboundSentinelConverter("poison"));

        var node = fixture.Node(nameof(WriteIntegrityChild.Value));

        // Act
        fixture.Child.Value = "poison";

        // Assert: nothing retries the change, so the node serves its previous value from here on. It must
        // stop claiming that value is current, which is the rule the inbound write path already applies to
        // this exact condition. The cleared mask is what says the drop reached the monitored items rather
        // than sitting on the node waiting for a flush that only a later change would bring.
        await AsyncTestHelpers.WaitUntilAsync(
            () => node.StatusCode == StatusCodes.UncertainLastUsableValue &&
                  node.ChangeMasks == NodeStateChangeMasks.None,
            timeout: TimeSpan.FromSeconds(10),
            message: "the node should have dropped to UncertainLastUsableValue and been flushed");
        Assert.Equal(WriteIntegrityFixture.InitialValue, node.Value);
    }

    [Fact]
    public async Task WhenOneChangesConversionThrows_ThenTheRestOfTheBatchIsStillWritten()
    {
        // Arrange: the server has one batch per flush, so a throw that escapes the loop costs every
        // property in it rather than only the one that could not be converted.
        await using var fixture = await WriteIntegrityFixture.StartAsync(
            _output, valueConverter: new ThrowOnOutboundSentinelConverter("poison"));

        // Act: both properties are written within one buffer window, the poisoned one first.
        fixture.Child.Value = "poison";
        fixture.Child.Other = "batch-survivor";

        // Assert: the sibling reaching its node is what proves the loop kept going.
        await AsyncTestHelpers.WaitUntilAsync(
            () => Equals(fixture.Node(nameof(WriteIntegrityChild.Other)).Value, "batch-survivor"),
            timeout: TimeSpan.FromSeconds(10),
            message: "the rest of the batch should still have reached its nodes");

        // The merger has already marked the poisoned property published, so nothing retries it: its node
        // keeps the value it had until the property changes again.
        Assert.Equal(WriteIntegrityFixture.InitialValue, fixture.Node(nameof(WriteIntegrityChild.Value)).Value);
    }
}
