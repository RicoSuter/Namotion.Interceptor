using System.Diagnostics;
using Namotion.Interceptor.ConnectorTester.Snapshot;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

public sealed record ConvergenceOutcome(
    bool Converged,
    TimeSpan Elapsed,
    IReadOnlyList<(string Name, string Snapshot)> Snapshots);

/// <summary>
/// Polls participant snapshots at SnapshotPollInterval until they all match (PASS)
/// or ConvergenceTimeout elapses (FAIL). Always captures at least one snapshot before
/// timing out (bug fix #5: previously a timeout shorter than the poll interval caused
/// the poll loop body to never run, returning FAIL with no snapshot taken).
/// </summary>
public sealed class ConvergenceChecker
{
    private readonly IReadOnlyDictionary<string, Func<string>> _participants;
    private readonly TimeSpan _convergenceTimeout;
    private readonly TimeSpan _snapshotPollInterval;

    public ConvergenceChecker(
        IReadOnlyDictionary<string, Func<string>> participants,
        TimeSpan convergenceTimeout,
        TimeSpan snapshotPollInterval)
    {
        _participants = participants;
        _convergenceTimeout = convergenceTimeout;
        _snapshotPollInterval = snapshotPollInterval;
    }

    public async Task<ConvergenceOutcome> WaitForConvergenceAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<(string Name, string Snapshot)> latestSnapshots;

        while (true)
        {
            latestSnapshots = CaptureAllSnapshots();

            var referenceSubjects = SnapshotComparer.ParseSubjects(latestSnapshots[0].Snapshot);
            if (latestSnapshots.All(snapshot => SnapshotComparer.SnapshotsMatch(referenceSubjects, snapshot.Snapshot)))
            {
                return new ConvergenceOutcome(Converged: true, stopwatch.Elapsed, latestSnapshots);
            }

            if (stopwatch.Elapsed >= _convergenceTimeout)
            {
                return new ConvergenceOutcome(Converged: false, stopwatch.Elapsed, latestSnapshots);
            }

            try
            {
                await Task.Delay(_snapshotPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ConvergenceOutcome(Converged: false, stopwatch.Elapsed, latestSnapshots);
            }
        }
    }

    private List<(string Name, string Snapshot)> CaptureAllSnapshots()
    {
        return _participants
            .Select(participant => (participant.Key, participant.Value()))
            .ToList();
    }
}
