using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Snapshot;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Runs after a cycle fails to converge: writes per-participant JSON snapshot files for
/// out-of-band diff investigation, logs each diverged property with values and timestamps,
/// and runs a re-sync diagnostic that applies the reference participant's complete state
/// to each diverged participant. Mutates participant state intentionally; safe to call only
/// when engines are paused (today's invariant from VerificationEngine).
/// </summary>
public sealed class FailureDiagnostics
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _runDirectory;
    private readonly IReadOnlyDictionary<string, TestNode> _participants;
    private readonly ILogger _logger;

    public FailureDiagnostics(
        string runDirectory,
        IReadOnlyDictionary<string, TestNode> participants,
        ILogger logger)
    {
        _runDirectory = runDirectory;
        _participants = participants;
        _logger = logger;
    }

    public async Task RunAsync(
        int cycleNumber,
        IReadOnlyList<(string Name, string Snapshot)> snapshots,
        CancellationToken cancellationToken)
    {
        await WriteFailureSnapshotsAsync(cycleNumber, snapshots, cancellationToken);
        LogPropertyDiffs(snapshots);
        LogReSyncCheck(snapshots);
    }

    /// <summary>
    /// Writes formatted JSON snapshots to disk for each participant, so failures can
    /// be diffed with any text tool. Runs only on convergence failure; never replaces
    /// the failure signal.
    /// </summary>
    private async Task WriteFailureSnapshotsAsync(
        int cycleNumber,
        IReadOnlyList<(string Name, string Snapshot)> snapshots,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_runDirectory);

            foreach (var snapshot in snapshots)
            {
                var fileName = $"cycle-{cycleNumber:D4}-fail-{snapshot.Name}.json";
                var filePath = Path.Combine(_runDirectory, fileName);

                // Re-serialize with indentation for readability.
                var node = JsonNode.Parse(snapshot.Snapshot);
                var formatted = node?.ToJsonString(IndentedJsonOptions) ?? snapshot.Snapshot;

                await File.WriteAllTextAsync(filePath, formatted, cancellationToken);
                _logger.LogInformation("Snapshot [{Name}] written to {FilePath}", snapshot.Name, filePath);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to write failure snapshots to disk");
        }
    }

    /// <summary>
    /// Diffs the snapshots and logs each diverged property with values and timestamps.
    /// All data comes from the normalized snapshot JSON: Value-kind timestamps are
    /// preserved by <see cref="SnapshotComparer.Capture"/>, so a missing timestamp
    /// field means the property was never written via the interceptor chain on that
    /// participant. Structural-kind properties (Object/Collection/Dictionary) have
    /// their timestamps stripped during normalization and are not informative here.
    /// </summary>
    private void LogPropertyDiffs(IReadOnlyList<(string Name, string Snapshot)> snapshots)
    {
        try
        {
            if (snapshots.Count < 2)
            {
                return;
            }

            var referenceName = snapshots[0].Name;
            var referenceSnapshot = snapshots[0].Snapshot;

            for (var i = 1; i < snapshots.Count; i++)
            {
                var otherName = snapshots[i].Name;
                var otherSnapshot = snapshots[i].Snapshot;

                foreach (var entry in SnapshotDiffer.Diff(referenceName, referenceSnapshot, otherName, otherSnapshot))
                {
                    LogDiffEntry(entry, referenceName, otherName);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to log property diffs with timestamps");
        }
    }

    private void LogDiffEntry(SnapshotDiffEntry entry, string referenceName, string otherName)
    {
        switch (entry.Kind)
        {
            case SnapshotDiffKind.SubjectMissingFromOther:
                _logger.LogError(
                    "  Subject {SubjectId}: present in {Reference}, missing from {Other}",
                    entry.SubjectId, referenceName, otherName);
                break;
            case SnapshotDiffKind.SubjectMissingFromReference:
                _logger.LogError(
                    "  Subject {SubjectId}: missing from {Reference}, present in {Other}",
                    entry.SubjectId, referenceName, otherName);
                break;
            case SnapshotDiffKind.PropertyMissingFromOther:
                _logger.LogError(
                    "  {SubjectId}.{Property}: present in {Reference}, missing from {Other}",
                    entry.SubjectId, entry.PropertyName, referenceName, otherName);
                break;
            case SnapshotDiffKind.PropertyMissingFromReference:
                _logger.LogError(
                    "  {SubjectId}.{Property}: missing from {Reference}, present in {Other}",
                    entry.SubjectId, entry.PropertyName, referenceName, otherName);
                break;
            case SnapshotDiffKind.PropertyDiffers:
                _logger.LogError(
                    "  {SubjectId}.{Property}: {Reference}={ReferenceSummary}, {Other}={OtherSummary}",
                    entry.SubjectId, entry.PropertyName,
                    referenceName, entry.ReferenceSummary,
                    otherName, entry.OtherSummary);
                break;
        }
    }

    /// <summary>
    /// Re-sync diagnostic. Takes the reference participant's complete update and applies
    /// it to each diverged participant, then re-compares.
    /// "Match after re-apply" => suspect connector wire (lost or out-of-order messages).
    /// "Still diverged" => suspect snapshot logic, ApplySubjectUpdate, or the model.
    /// Mutates participant state intentionally; runs only after the cycle has failed
    /// and the process is shutting down.
    /// </summary>
    private void LogReSyncCheck(IReadOnlyList<(string Name, string Snapshot)> snapshots)
    {
        try
        {
            var referenceRoot = _participants[snapshots[0].Name];
            var completeUpdate = SubjectUpdate.CreateCompleteUpdate(referenceRoot, []);

            for (var i = 1; i < snapshots.Count; i++)
            {
                if (SnapshotComparer.SnapshotsMatch(snapshots[i].Snapshot, snapshots[0].Snapshot))
                {
                    continue;
                }

                var otherRoot = _participants[snapshots[i].Name];
                otherRoot.ApplySubjectUpdate(completeUpdate, DefaultSubjectFactory.Instance);

                // Reference is paused and not mutated since snapshots[0] was taken; re-using
                // that string avoids redundant work and a subtle correctness footgun if a
                // future change introduced reference-side mutation between cycles.
                var otherReSnapshot = SnapshotComparer.Capture(otherRoot);

                if (SnapshotComparer.SnapshotsMatch(snapshots[0].Snapshot, otherReSnapshot))
                {
                    _logger.LogWarning(
                        "Re-sync check: {Participant} converged after applying reference complete update -> transient delivery gap",
                        snapshots[i].Name);
                }
                else
                {
                    _logger.LogError(
                        "Re-sync check: {Participant} still diverged after applying reference complete update -> suspect snapshot logic, ApplySubjectUpdate, or model",
                        snapshots[i].Name);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to perform re-sync check");
        }
    }
}
