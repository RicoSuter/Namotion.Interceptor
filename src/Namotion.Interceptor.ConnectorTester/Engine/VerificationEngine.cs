using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Logging;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Reporting;
using Namotion.Interceptor.ConnectorTester.Snapshot;

namespace Namotion.Interceptor.ConnectorTester.Engine;

public enum CycleResult { Pass, Fail }

/// <summary>
/// Top-level orchestrator. Runs repeating mutate/converge cycles.
/// On convergence failure, exits the process with non-zero exit code.
/// </summary>
public class VerificationEngine : BackgroundService
{
    private static readonly TimeSpan SnapshotPollInterval = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly CsvFile<CycleCsvRow> _cyclesCsv;
    private readonly CsvFile<ChaosEventCsvRow> _chaosEventsCsv;
    private readonly string _findingsLogPath;
    private readonly string _runDirectory;

    private readonly ConnectorTesterConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly Dictionary<string, TestNode> _participants;
    private readonly List<MutationEngine> _mutationEngines;
    private readonly List<ChaosEngine> _chaosEngines;
    private readonly CycleLoggerProvider? _cycleLoggerProvider;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger _logger;

    private int _cycleNumber;
    private bool _failed;

    /// <summary>
    /// Whether the last cycle failed to converge. Used to set the process exit code.
    /// </summary>
    public bool Failed => _failed;

    public VerificationEngine(
        ConnectorTesterConfiguration configuration,
        TestCycleCoordinator coordinator,
        Dictionary<string, TestNode> participants,
        List<MutationEngine> mutationEngines,
        List<ChaosEngine> chaosEngines,
        CycleLoggerProvider? cycleLoggerProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger logger,
        string runDirectory)
    {
        _configuration = configuration;
        _coordinator = coordinator;
        _participants = participants;
        _mutationEngines = mutationEngines;
        _chaosEngines = chaosEngines;
        _cycleLoggerProvider = cycleLoggerProvider;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _runDirectory = runDirectory;
        _cyclesCsv = CyclesCsv.Create(Path.Combine(runDirectory, "cycles.csv"));
        _chaosEventsCsv = ChaosEventsCsv.Create(Path.Combine(runDirectory, "chaos-events.csv"));
        _findingsLogPath = Path.Combine(runDirectory, "findings.log");
    }

    public override void Dispose()
    {
        _cyclesCsv.Dispose();
        _chaosEventsCsv.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cyclesCsv.WriteHeader();
        _chaosEventsCsv.WriteHeader();

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _logger.LogInformation("""
            === Connector Tester Configuration ===
            Connector: {Connector}
            MutatePhaseDuration: {MutatePhaseDuration}
            ConvergenceTimeout: {ConvergenceTimeout}
            Participants: {Participants}
            """,
            _configuration.Connector,
            _configuration.MutatePhaseDuration,
            _configuration.ConvergenceTimeout,
            string.Join(", ", _participants.Select(p => p.Key)));
        foreach (var engine in _mutationEngines)
        {
            _logger.LogInformation("  {Name}: {Rate} value mutations/sec, {StructuralRate} structural mutations/sec",
                engine.Name, engine.ValueMutationRate, engine.StructuralMutationRate);
        }

        if (_configuration.ChaosProfiles.Count > 0)
        {
            _logger.LogInformation("  Chaos profiles ({Count}): {Profiles}",
                _configuration.ChaosProfiles.Count,
                string.Join(" -> ", _configuration.ChaosProfiles.Select(p => p.Name)));
        }

        foreach (var profile in _configuration.ChaosProfiles)
        {
            foreach (var participant in profile.Participants)
            {
                if (_chaosEngines.All(e => e.TargetName != participant))
                {
                    _logger.LogWarning(
                        "Chaos profile '{Profile}' references '{Participant}' which has no chaos engine (no Chaos config or not a known participant). It will be ignored.",
                        profile.Name, participant);
                }
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _cycleNumber++;
            _coordinator.SetCycle(_cycleNumber);

            foreach (var engine in _mutationEngines)
                engine.ResetCounters();
            foreach (var engine in _chaosEngines)
                engine.ResetCounters();

            var activeProfileName = ApplyChaosProfile();

            _cycleLoggerProvider?.StartNewCycle(_cycleNumber);

            var cycleStopwatch = Stopwatch.StartNew();

            _coordinator.Resume();
            var profileLabel = activeProfileName != null ? $" [profile: {activeProfileName}]" : "";
            _logger.LogInformation("=== Cycle {Cycle}: Mutate phase started ({Duration}){Profile} ===",
                _cycleNumber, _configuration.MutatePhaseDuration, profileLabel);

            await Task.Delay(_configuration.MutatePhaseDuration, stoppingToken);

            _coordinator.Pause();
            _logger.LogInformation("=== Cycle {Cycle}: Converge phase started ===", _cycleNumber);

            foreach (var chaosEngine in _chaosEngines)
            {
                await chaosEngine.RecoverActiveDisruptionAsync(stoppingToken);
            }

            // OPC UA needs ~15-20s: server restart + port bind, client keep-alive
            // detection (up to 5s), reconnect handler (5s), session + subscription setup.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

            var convergeStopwatch = Stopwatch.StartNew();
            var converged = false;
            var maxPolls = (int)(_configuration.ConvergenceTimeout / SnapshotPollInterval);

            for (var poll = 0; poll < maxPolls; poll++)
            {
                var snapshots = CaptureAllSnapshots();

                var referenceSubjects = SnapshotComparer.ParseSubjects(snapshots[0].Snapshot);
                if (snapshots.All(snapshot => SnapshotComparer.SnapshotsMatch(referenceSubjects, snapshot.Snapshot)))
                {
                    convergeStopwatch.Stop();
                    cycleStopwatch.Stop();

                    LogFindings(snapshots, convergeStopwatch.Elapsed);

                    WriteStatistics(cycleStopwatch.Elapsed, convergeStopwatch.Elapsed, CycleResult.Pass);
                    CompactHeapAndLogCycle(activeProfileName, CycleResult.Pass, cycleStopwatch.Elapsed, convergeStopwatch.Elapsed);
                    _logger.LogInformation(
                        "=== Cycle {Cycle}: PASS (converged in {ConvergeTime:F1}s, cycle {CycleTime:F0}s) ===",
                        _cycleNumber, convergeStopwatch.Elapsed.TotalSeconds, cycleStopwatch.Elapsed.TotalSeconds);

                    _cycleLoggerProvider?.FinishCycle(_cycleNumber, CycleResult.Pass);
                    converged = true;
                    break;
                }

                await Task.Delay(SnapshotPollInterval, stoppingToken);
            }

            if (!converged)
            {
                cycleStopwatch.Stop();
                convergeStopwatch.Stop();

                var snapshots = CaptureAllSnapshots();

                _logger.LogError("=== Cycle {Cycle}: FAIL (did not converge within {Timeout}) ===",
                    _cycleNumber, _configuration.ConvergenceTimeout);

                await LogFailureSnapshotsAsync(snapshots, stoppingToken);

                LogPropertyDiffsWithTimestamps(snapshots);
                LogReSyncCheck(snapshots);

                WriteStatistics(cycleStopwatch.Elapsed, convergeStopwatch.Elapsed, CycleResult.Fail);
                CompactHeapAndLogCycle(activeProfileName, CycleResult.Fail, cycleStopwatch.Elapsed, convergeStopwatch.Elapsed);
                _cycleLoggerProvider?.FinishCycle(_cycleNumber, CycleResult.Fail);

                _failed = true;
                _applicationLifetime.StopApplication();
                return;
            }
        }
    }

    private List<(string Name, string Snapshot)> CaptureAllSnapshots()
    {
        return _participants
            .Select(participant => (
                Name: participant.Key,
                Snapshot: SnapshotComparer.Capture(participant.Value)))
            .ToList();
    }

    private void LogFindings(List<(string Name, string Snapshot)> snapshots, TimeSpan convergeTime)
    {
        try
        {
            var findings = new List<string>();

            // 1. Convergence time anomaly (>10s with no chaos active)
            var chaosActive = _chaosEngines.Any(engine => engine.ChaosEventCount > 0);
            if (convergeTime.TotalSeconds > 10 && !chaosActive)
            {
                findings.Add($"slow-convergence: {convergeTime.TotalSeconds:F1}s with no chaos active");
            }

            // 2. Null-timestamp forgiveness (JSON walk needed)
            for (var i = 1; i < snapshots.Count; i++)
            {
                var timestampFindings = SnapshotDiffer.CollectFindings(
                    snapshots[0].Name, snapshots[0].Snapshot,
                    snapshots[i].Name, snapshots[i].Snapshot);

                if (timestampFindings is { Count: > 0 })
                {
                    findings.AddRange(timestampFindings.Select(finding => $"null-timestamp: {finding}"));
                }
            }

            if (findings.Count == 0)
            {
                return;
            }

            _logger.LogWarning("Cycle {Cycle}: {Count} finding(s)", _cycleNumber, findings.Count);

            var lines = new List<string>
            {
                $"[{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}] Cycle {_cycleNumber}: {findings.Count} finding(s)"
            };
            lines.AddRange(findings.Select(finding => $"  {finding}"));
            lines.Add("");
            File.AppendAllLines(_findingsLogPath, lines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log findings");
        }
    }

    private void WriteStatistics(TimeSpan cycleDuration, TimeSpan convergeDuration, CycleResult result)
    {
        var totalMutations = _mutationEngines.Sum(engine => engine.ValueMutationCount);
        var totalChaos = _chaosEngines.Sum(engine => engine.ChaosEventCount);

        _logger.LogInformation("""
            --- Cycle {Cycle} Statistics ---
            Duration: {CycleDuration:F0}s (converged in {ConvergeDuration:F1}s)
            Total mutations: {TotalMutations:N0} | Total chaos events: {TotalChaos}
            Result: {Result}
            """,
            _cycleNumber, cycleDuration.TotalSeconds, convergeDuration.TotalSeconds,
            totalMutations, totalChaos, result);

        // Per-participant breakdown
        foreach (var engine in _mutationEngines)
        {
            _logger.LogInformation("  {Name}: {Values:N0} value mutations, {Structural:N0} structural mutations",
                engine.Name, engine.ValueMutationCount, engine.StructuralMutationCount);
        }

        // Chaos event timeline
        foreach (var engine in _chaosEngines)
        {
            foreach (var record in engine.EventHistory)
            {
                _logger.LogInformation("  {Name}: {FaultType} at {Time:HH:mm:ss} ({Duration:F1}s)",
                    engine.TargetName, record.FaultType, record.DisruptedAt.LocalDateTime, record.Duration.TotalSeconds);
            }
        }
    }

    private void CompactHeapAndLogCycle(string? profileName, CycleResult result, TimeSpan cycleDuration, TimeSpan convergeDuration)
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var process = Process.GetCurrentProcess();
        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var processMb = process.WorkingSet64 / (1024.0 * 1024.0);

        var totalValueMutations = _mutationEngines.Sum(e => e.ValueMutationCount);
        var totalStructuralMutations = _mutationEngines.Sum(e => e.StructuralMutationCount);
        var totalChaosEvents = _chaosEngines.Sum(e => e.ChaosEventCount);

        var mutateSeconds = _configuration.MutatePhaseDuration.TotalSeconds;

        try
        {
            _cyclesCsv.AppendRow(new CycleCsvRow(
                Timestamp: DateTimeOffset.UtcNow,
                Cycle: _cycleNumber,
                Result: result,
                Profile: profileName ?? "",
                MutateSeconds: mutateSeconds,
                ConvergeSeconds: convergeDuration.TotalSeconds,
                CycleSeconds: cycleDuration.TotalSeconds,
                ValueMutations: totalValueMutations,
                StructuralMutations: totalStructuralMutations,
                ChaosEvents: totalChaosEvents,
                HeapMb: heapMb,
                ProcessMb: processMb));

            foreach (var engine in _chaosEngines)
            {
                foreach (var record in engine.EventHistory)
                {
                    _chaosEventsCsv.AppendRow(new ChaosEventCsvRow(
                        Timestamp: record.DisruptedAt.UtcDateTime,
                        Cycle: _cycleNumber,
                        Participant: engine.TargetName,
                        FaultType: record.FaultType,
                        DurationSeconds: record.Duration.TotalSeconds));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append to cycles.csv or chaos-events.csv");
        }
    }

    /// <summary>
    /// Writes formatted JSON snapshots to disk for each participant, so failures can
    /// be diffed with any text tool. Runs only on convergence failure; never replaces
    /// the failure signal.
    /// </summary>
    private async Task LogFailureSnapshotsAsync(
        List<(string Name, string Snapshot)> snapshots,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_runDirectory);

            foreach (var snapshot in snapshots)
            {
                var fileName = $"cycle-{_cycleNumber:D4}-fail-{snapshot.Name}.json";
                var filePath = Path.Combine(_runDirectory, fileName);

                // Re-serialize with indentation for readability.
                var node = JsonNode.Parse(snapshot.Snapshot);
                var formatted = node?.ToJsonString(IndentedJsonOptions) ?? snapshot.Snapshot;

                await File.WriteAllTextAsync(filePath, formatted, cancellationToken);
                _logger.LogInformation("Snapshot [{Name}] written to {FilePath}", snapshot.Name, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write failure snapshots to disk");
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
    private void LogPropertyDiffsWithTimestamps(List<(string Name, string Snapshot)> snapshots)
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
    private void LogReSyncCheck(List<(string Name, string Snapshot)> snapshots)
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to perform re-sync check");
        }
    }

    private string? ApplyChaosProfile()
    {
        var profiles = _configuration.ChaosProfiles;
        if (profiles.Count == 0)
        {
            return null;
        }

        var profile = profiles[(_cycleNumber - 1) % profiles.Count];

        foreach (var engine in _chaosEngines)
        {
            engine.Enabled = profile.Participants.Contains(engine.TargetName);
        }

        return profile.Name;
    }
}
