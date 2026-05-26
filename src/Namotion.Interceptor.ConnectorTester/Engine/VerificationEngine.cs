using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Logging;
using Namotion.Interceptor.ConnectorTester.Model;

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

    private readonly string _cyclesLogPath;
    private readonly string _chaosEventsLogPath;
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
        _cyclesLogPath = Path.Combine(runDirectory, "cycles.csv");
        _chaosEventsLogPath = Path.Combine(runDirectory, "chaos-events.csv");
        _findingsLogPath = Path.Combine(runDirectory, "findings.log");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cyclesHeader = string.Format(
            "{0,24}, {1,6}, {2,6}, {3,20}, {4,14}, {5,16}, {6,12}, {7,16}, {8,20}, {9,12}, {10,10}, {11,10}",
            "Timestamp", "Cycle", "Result", "Profile", "MutateSeconds", "ConvergeSeconds", "CycleSeconds",
            "ValueMutations", "StructuralMutations", "ChaosEvents",
            "HeapMB", "ProcessMB");
        await File.WriteAllTextAsync(_cyclesLogPath, cyclesHeader + Environment.NewLine, stoppingToken);

        var chaosHeader = string.Format(
            "{0,24}, {1,6}, {2,16}, {3,12}, {4,16}",
            "Timestamp", "Cycle", "Participant", "FaultType", "DurationSeconds");
        await File.WriteAllTextAsync(_chaosEventsLogPath, chaosHeader + Environment.NewLine, stoppingToken);

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
                var timestampFindings = SnapshotComparer.CollectFindings(
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
        var line = string.Format(CultureInfo.InvariantCulture,
            "{0,24:yyyy-MM-ddTHH:mm:ss.fffZ}, {1,6}, {2,6}, {3,20}, {4,14:F1}, {5,16:F1}, {6,12:F1}, {7,16}, {8,20}, {9,12}, {10,10:F1}, {11,10:F1}",
            DateTimeOffset.UtcNow, _cycleNumber, result, profileName ?? "",
            mutateSeconds, convergeDuration.TotalSeconds, cycleDuration.TotalSeconds,
            totalValueMutations, totalStructuralMutations, totalChaosEvents,
            heapMb, processMb);

        try
        {
            File.AppendAllText(_cyclesLogPath, line + Environment.NewLine);

            foreach (var engine in _chaosEngines)
            {
                foreach (var record in engine.EventHistory)
                {
                    var chaosLine = string.Format(CultureInfo.InvariantCulture,
                        "{0,24:yyyy-MM-ddTHH:mm:ss.fffZ}, {1,6}, {2,16}, {3,12}, {4,16:F1}",
                        record.DisruptedAt.UtcDateTime, _cycleNumber, engine.TargetName,
                        record.FaultType, record.Duration.TotalSeconds);
                    File.AppendAllText(_chaosEventsLogPath, chaosLine + Environment.NewLine);
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

            var reference = SnapshotComparer.ParseSubjects(snapshots[0].Snapshot);
            if (reference is null)
            {
                return;
            }
            var referenceName = snapshots[0].Name;

            for (var i = 1; i < snapshots.Count; i++)
            {
                var other = SnapshotComparer.ParseSubjects(snapshots[i].Snapshot);
                if (other is null)
                {
                    continue;
                }
                var otherName = snapshots[i].Name;

                foreach (var (subjectId, refSubjectNode) in reference)
                {
                    if (other[subjectId] is not JsonObject otherProperties)
                    {
                        _logger.LogError(
                            "  Subject {SubjectId}: present in {Reference}, missing from {Other}",
                            subjectId, referenceName, otherName);
                        continue;
                    }

                    var refProperties = refSubjectNode!.AsObject();
                    foreach (var (propertyName, refPropertyNode) in refProperties)
                    {
                        if (otherProperties[propertyName] is not JsonObject otherProp)
                        {
                            _logger.LogError(
                                "  {SubjectId}.{Property}: present in {Reference}, missing from {Other}",
                                subjectId, propertyName, referenceName, otherName);
                            continue;
                        }

                        var refProp = refPropertyNode!.AsObject();
                        if (SnapshotComparer.PropertiesMatch(refProp, otherProp))
                        {
                            continue;
                        }

                        _logger.LogError(
                            "  {SubjectId}.{Property}: {Reference}={RefSummary}, {Other}={OtherSummary}",
                            subjectId, propertyName,
                            referenceName, SummarizeProperty(refProp),
                            otherName, SummarizeProperty(otherProp));
                    }
                }

                // Also report subjects present only on the other side, and properties
                // present only on the other side within shared subjects.
                foreach (var (subjectId, otherSubjectNode) in other)
                {
                    if (!reference.ContainsKey(subjectId))
                    {
                        _logger.LogError(
                            "  Subject {SubjectId}: missing from {Reference}, present in {Other}",
                            subjectId, referenceName, otherName);
                        continue;
                    }

                    if (otherSubjectNode is not JsonObject otherSharedProperties)
                    {
                        continue;
                    }

                    var refSharedProperties = reference[subjectId]!.AsObject();
                    foreach (var (propertyName, _) in otherSharedProperties)
                    {
                        if (!refSharedProperties.ContainsKey(propertyName))
                        {
                            _logger.LogError(
                                "  {SubjectId}.{Property}: missing from {Reference}, present in {Other}",
                                subjectId, propertyName, referenceName, otherName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log property diffs with timestamps");
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

    private static string SummarizeProperty(JsonObject property)
    {
        var kind = property[SnapshotComparer.KindKey]?.GetValue<string>() ?? "?";
        return kind switch
        {
            "Value" => FormatValueSummary(property),
            "Object" => $"Object id={property[SnapshotComparer.IdKey]?.ToJsonString() ?? "null"}",
            "Collection" or "Dictionary" =>
                $"{kind} count={property[SnapshotComparer.CountKey]?.ToJsonString() ?? "?"} " +
                $"items={property[SnapshotComparer.ItemsKey]?.ToJsonString() ?? "[]"}",
            _ => property.ToJsonString()
        };
    }

    private static string FormatValueSummary(JsonObject property)
    {
        var value = property[SnapshotComparer.ValueKey]?.ToJsonString() ?? "null";
        var timestamp = property[SnapshotComparer.TimestampKey]?.GetValue<string>() ?? "never";
        return $"{value} (written {timestamp})";
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
