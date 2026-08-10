using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// How a read-after-write ranks the value it read back against what the property holds now. Two
/// questions in two domains: a local write that landed after ours is ranked by revision, and a value
/// the server produced is ranked by the server's own timestamps.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaReadAfterWriteTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaReadAfterWriteTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WhenNothingLandedAfterTheWrite_ThenTheReadBackIsApplied()
    {
        // Arrange
        await using var fixture = await ReadAfterWriteFixture.StartAsync(_output);

        // Act
        fixture.ClientChild.Trigger = "command";

        // Assert: the local write is synchronous, so this pins that the model really moved off the
        // server's value and the revert below is the read-back rather than a value that never changed.
        Assert.Equal("command", fixture.ClientChild.Trigger);

        await fixture.WaitForScheduledReadBackAsync();
        await fixture.WaitForAppliedReadBackAsync();

        // The server's node never changes value or status, so no subscription notification exists for
        // this value; the applied-read-back count and the model agreeing is the read-back's own doing.
        Assert.Equal(ReadAfterWriteFixture.ServerValue, fixture.ClientChild.Trigger);
    }

    [Fact]
    public async Task WhenALocalWriteCommitsAfterTheReadBackWasScheduled_ThenItSurvives()
    {
        // Arrange
        await using var fixture = await ReadAfterWriteFixture.StartAsync(_output);

        // Act: the second write commits locally well before its own flush tick, so the read-back that
        // the first write scheduled is still the one that runs, and it is older than the model.
        fixture.ClientChild.Trigger = "first";
        await fixture.WaitForScheduledReadBackAsync();
        fixture.ClientChild.Trigger = "newer";

        // Assert
        await fixture.WaitForSkippedReadBackAsync();
        Assert.Equal("newer", fixture.ClientChild.Trigger);
    }

    [Fact]
    public async Task WhenASourceValueCommitsAfterTheWrite_ThenItSurvives()
    {
        // Arrange
        await using var fixture = await ReadAfterWriteFixture.StartAsync(_output);
        fixture.ClientChild.Trigger = "first";
        await fixture.WaitForScheduledReadBackAsync();

        // Act: a status change is the one thing the Status trigger still reports, so this reaches the
        // client as a subscription notification carrying a source timestamp of the server's own.
        var sourceTimestamp = DateTime.UtcNow;
        fixture.PublishToNode("from-server", StatusCodes.UncertainLastUsableValue, sourceTimestamp);
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ClientChild.Trigger == "from-server",
            message: "the status change should carry the server's value to the client");

        // The node then moves back to an older value without touching the status, so the read-back
        // returns something the notification already superseded and no second notification follows.
        fixture.PublishToNode(
            ReadAfterWriteFixture.ServerValue,
            StatusCodes.UncertainLastUsableValue,
            sourceTimestamp.AddSeconds(-1));

        // Assert
        await fixture.WaitForSkippedReadBackAsync();
        Assert.Equal("from-server", fixture.ClientChild.Trigger);
    }

    [Fact]
    public async Task WhenAWriteInTheBatchIsRefused_ThenNoReadBackIsScheduledForIt()
    {
        // Arrange
        await using var fixture = await ReadAfterWriteFixture.StartAsync(_output);

        // Act: both leave in one flush, so the batch comes back a partial failure carrying the refusal
        fixture.ClientChild.Trigger = "command";
        fixture.ClientChild.Refused = "command";

        // Assert: the accepted write's read-back running settles the batch, since both would have been
        // scheduled in the same synchronous step well before either could run.
        await fixture.WaitForAppliedReadBackAsync();
        Assert.Equal(1L, fixture.ScheduledReadBackCount);

        // The refused write is still queued for retry, so a read-back for it would have reverted the
        // model to the server's pre-write value only for the retry to push it back again.
        Assert.Equal("command", fixture.ClientChild.Refused);
    }

    [Fact]
    public async Task WhenALocalWriteCommitsWhileTheWriteRequestIsUnacknowledged_ThenItSurvives()
    {
        // Arrange
        await using var fixture = await ReadAfterWriteFixture.StartAsync(_output);

        // Act: "newer" commits after the request carrying "first" was built and before it is
        // acknowledged, which is the window a revision re-read after the write would fold in.
        fixture.GateOutboundWriteOf("first");
        fixture.ClientChild.Trigger = "first";
        fixture.WaitUntilOutboundWriteIsGated();
        fixture.ClientChild.Trigger = "newer";
        fixture.ReleaseOutboundWrite();

        // Assert
        await fixture.WaitForSkippedReadBackAsync();
        Assert.Equal("newer", fixture.ClientChild.Trigger);
    }
}
