using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class ConvergenceCheckerTests
{
    private const string IdenticalSnapshot = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";
    private const string DifferentSnapshot = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":2}}}}""";

    [Fact]
    public async Task WhenSnapshotsAlwaysMatchOnFirstPoll_ThenConvergesImmediately()
    {
        // Arrange
        var participants = new Dictionary<string, Func<string>>
        {
            ["server"] = () => IdenticalSnapshot,
            ["client"] = () => IdenticalSnapshot
        };
        var checker = new ConvergenceChecker(
            participants,
            convergenceTimeout: TimeSpan.FromSeconds(60),
            snapshotPollInterval: TimeSpan.FromSeconds(5));

        // Act
        var outcome = await checker.WaitForConvergenceAsync(CancellationToken.None);

        // Assert
        Assert.True(outcome.Converged);
        Assert.Equal(2, outcome.Snapshots.Count);
    }

    [Fact]
    public async Task WhenConvergenceTimeoutIsShorterThanPollInterval_ThenAlwaysCapturesAtLeastOneSnapshot()
    {
        // Bug fix #5: previously `maxPolls = (4s / 5s) = 0` meant the poll loop body never ran,
        // and the cycle failed immediately without a snapshot. Now the checker always takes one
        // snapshot and only declines further attempts if the stopwatch has exceeded the timeout.
        var participants = new Dictionary<string, Func<string>>
        {
            ["server"] = () => IdenticalSnapshot,
            ["client"] = () => IdenticalSnapshot
        };
        var checker = new ConvergenceChecker(
            participants,
            convergenceTimeout: TimeSpan.FromSeconds(4),
            snapshotPollInterval: TimeSpan.FromSeconds(5));

        // Act
        var outcome = await checker.WaitForConvergenceAsync(CancellationToken.None);

        // Assert: even though timeout < poll interval, we still get a converged outcome.
        Assert.True(outcome.Converged);
        Assert.Equal(2, outcome.Snapshots.Count);
    }

    [Fact]
    public async Task WhenSnapshotsNeverConverge_ThenTimesOutAndReturnsLastSnapshots()
    {
        // Arrange
        var participants = new Dictionary<string, Func<string>>
        {
            ["server"] = () => IdenticalSnapshot,
            ["client"] = () => DifferentSnapshot
        };
        var checker = new ConvergenceChecker(
            participants,
            convergenceTimeout: TimeSpan.FromMilliseconds(200),
            snapshotPollInterval: TimeSpan.FromMilliseconds(50));

        // Act
        var outcome = await checker.WaitForConvergenceAsync(CancellationToken.None);

        // Assert
        Assert.False(outcome.Converged);
        Assert.Equal(2, outcome.Snapshots.Count);
    }
}
