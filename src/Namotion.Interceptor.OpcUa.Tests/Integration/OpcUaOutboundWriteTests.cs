using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// What the client reports when one batch of a multi-batch flush fails, which is what decides whether
/// the batches behind it are attempted at all.
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
}
