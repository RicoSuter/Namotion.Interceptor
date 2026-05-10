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

    private const string CyclesLogPath = "logs/cycles.csv";

    private const string LogsDirectory = "logs";

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
        ILogger logger)
    {
        _configuration = configuration;
        _coordinator = coordinator;
        _participants = participants;
        _mutationEngines = mutationEngines;
        _chaosEngines = chaosEngines;
        _cycleLoggerProvider = cycleLoggerProvider;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CyclesLogPath)!);
        var header = string.Format(
            "{0,24}, {1,6}, {2,6}, {3,20}, {4,10}, {5,12}, {6,12}, {7,16}, {8,20}, {9,12}, {10,10}, {11,10}",
            "Timestamp", "Cycle", "Result", "Profile", "MutateSec", "ConvergeSec", "CycleSec",
            "ValueMutations", "StructuralMutations", "ChaosEvents",
            "HeapMB", "ProcessMB");
        
        await File.WriteAllTextAsync(CyclesLogPath, header + Environment.NewLine, stoppingToken);

        // Brief startup delay to let connectors initialize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Log configuration header
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

        // Validate chaos profile participant names
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

            // Reset counters
            foreach (var engine in _mutationEngines)
                engine.ResetCounters();
            foreach (var engine in _chaosEngines)
                engine.ResetCounters();

            var activeProfileName = ApplyChaosProfile();

            _cycleLoggerProvider?.StartNewCycle(_cycleNumber);

            var cycleStopwatch = Stopwatch.StartNew();

            // 1. Mutate phase
            _coordinator.Resume();
            var profileLabel = activeProfileName != null ? $" [profile: {activeProfileName}]" : "";
            _logger.LogInformation("=== Cycle {Cycle}: Mutate phase started ({Duration}){Profile} ===",
                _cycleNumber, _configuration.MutatePhaseDuration, profileLabel);

            await Task.Delay(_configuration.MutatePhaseDuration, stoppingToken);

            // 2. Transition to converge
            _coordinator.Pause();
            _logger.LogInformation("=== Cycle {Cycle}: Converge phase started ===", _cycleNumber);

            // Recover all active chaos disruptions
            foreach (var chaosEngine in _chaosEngines)
            {
                await chaosEngine.RecoverActiveDisruptionAsync(stoppingToken);
            }

            // Grace period for server startup, port binding, and client reconnection.
            // OPC UA needs ~15-20s: server restart + port bind, client keep-alive
            // detection (up to 5s), reconnect handler (5s), session + subscription setup.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

            // 3. Poll-compare snapshots
            var convergeStopwatch = Stopwatch.StartNew();
            var converged = false;
            var maxPolls = (int)(_configuration.ConvergenceTimeout / SnapshotPollInterval);

            for (var poll = 0; poll < maxPolls; poll++)
            {
                var snapshots = _participants
                    .Select(participant => (
                        Name: participant.Key,
                        Snapshot: SnapshotComparer.Capture(participant.Value)))
                    .ToList();

                if (snapshots.All(snapshot => SnapshotComparer.SnapshotsMatch(snapshots[0].Snapshot, snapshot.Snapshot)))
                {
                    convergeStopwatch.Stop();
                    cycleStopwatch.Stop();

                    WriteStatistics(cycleStopwatch.Elapsed, convergeStopwatch.Elapsed, "PASS");
                    CompactHeapAndLogCycle(activeProfileName, "PASS", cycleStopwatch.Elapsed, convergeStopwatch.Elapsed);
                    _logger.LogInformation(
                        "=== Cycle {Cycle}: PASS (converged in {ConvergeTime:F1}s, cycle {CycleTime:F0}s) ===",
                        _cycleNumber, convergeStopwatch.Elapsed.TotalSeconds, cycleStopwatch.Elapsed.TotalSeconds);

                    _cycleLoggerProvider?.FinishCycle(_cycleNumber, true);
                    converged = true;
                    break;
                }

                await Task.Delay(SnapshotPollInterval, stoppingToken);
            }

            if (!converged)
            {
                cycleStopwatch.Stop();
                convergeStopwatch.Stop();

                // Log failure details
                var snapshots = _participants
                    .Select(participant => (
                        Name: participant.Key,
                        Snapshot: SnapshotComparer.Capture(participant.Value)))
                    .ToList();

                _logger.LogError("=== Cycle {Cycle}: FAIL (did not converge within {Timeout}) ===",
                    _cycleNumber, _configuration.ConvergenceTimeout);

                // Per-participant snapshot files (canonical artifact for diffing)
                await LogFailureSnapshotsAsync(snapshots, stoppingToken);

                // Per-property diff log (values + write timestamps)
                LogPropertyDiffsWithTimestamps(snapshots);
                LogReSyncCheck(snapshots);

                WriteStatistics(cycleStopwatch.Elapsed, convergeStopwatch.Elapsed, "FAIL");
                CompactHeapAndLogCycle(activeProfileName, "FAIL", cycleStopwatch.Elapsed, convergeStopwatch.Elapsed);
                _cycleLoggerProvider?.FinishCycle(_cycleNumber, false);

                _failed = true;
                _applicationLifetime.StopApplication();
                return;
            }
        }
    }

    private void WriteStatistics(TimeSpan cycleDuration, TimeSpan convergeDuration, string result)
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

    private void CompactHeapAndLogCycle(string? profileName, string result, TimeSpan cycleDuration, TimeSpan convergeDuration)
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

        var mutateSec = _configuration.MutatePhaseDuration.TotalSeconds;
        var line = string.Format(CultureInfo.InvariantCulture,
            "{0,24:yyyy-MM-ddTHH:mm:ss.fffZ}, {1,6}, {2,6}, {3,20}, {4,10:F1}, {5,12:F1}, {6,12:F1}, {7,16}, {8,20}, {9,12}, {10,10:F1}, {11,10:F1}",
            DateTimeOffset.UtcNow, _cycleNumber, result, profileName ?? "",
            mutateSec, convergeDuration.TotalSeconds, cycleDuration.TotalSeconds,
            totalValueMutations, totalStructuralMutations, totalChaosEvents,
            heapMb, processMb);

        try
        {
            File.AppendAllText(CyclesLogPath, line + Environment.NewLine);
        }
        catch
        {
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
            Directory.CreateDirectory(LogsDirectory);

            foreach (var snapshot in snapshots)
            {
                var fileName = $"cycle{_cycleNumber:D3}-fail-{snapshot.Name}.json";
                var filePath = Path.Combine(LogsDirectory, fileName);

                // Re-serialize with indentation for readability.
                var node = JsonNode.Parse(snapshot.Snapshot);
                var formatted = node?.ToJsonString(IndentedJsonOptions) ?? snapshot.Snapshot;

                await File.WriteAllTextAsync(filePath, formatted, cancellationToken);
                _logger.LogError("Snapshot [{Name}] written to {FilePath}", snapshot.Name, filePath);
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

            var reference = JsonNode.Parse(snapshots[0].Snapshot)?["subjects"]?.AsObject();
            if (reference is null)
            {
                return;
            }
            var referenceName = snapshots[0].Name;

            for (var i = 1; i < snapshots.Count; i++)
            {
                var other = JsonNode.Parse(snapshots[i].Snapshot)?["subjects"]?.AsObject();
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
                        if (PropertyJsonsAreEffectivelyEqual(refProp, otherProp))
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

    /// <summary>
    /// Mirrors the null-timestamp rule from <see cref="SnapshotComparer.SnapshotsMatch"/>
    /// at the per-property level so the diff log treats convergence the same way
    /// the convergence check does (no false-positive diffs).
    /// </summary>
    private static bool PropertyJsonsAreEffectivelyEqual(JsonObject a, JsonObject b)
    {
        if (a.ToJsonString() == b.ToJsonString())
        {
            return true;
        }

        var keys = new HashSet<string>(a.Select(kvp => kvp.Key));
        keys.UnionWith(b.Select(kvp => kvp.Key));

        foreach (var key in keys)
        {
            var valueA = a[key];
            var valueB = b[key];

            if (key == "timestamp")
            {
                // Null on either side matches any value (NullTimestampTicks contract).
                if (valueA is not null && valueB is not null &&
                    valueA.ToJsonString() != valueB.ToJsonString())
                {
                    return false;
                }
            }
            else
            {
                if ((valueA is null) != (valueB is null) ||
                    (valueA is not null && valueA.ToJsonString() != valueB!.ToJsonString()))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string SummarizeProperty(JsonObject property)
    {
        var kind = property["kind"]?.GetValue<string>() ?? "?";
        return kind switch
        {
            "Value" => FormatValueSummary(property),
            "Object" => $"Object id={property["id"]?.ToJsonString() ?? "null"}",
            "Collection" or "Dictionary" => $"{kind} count={property["count"]?.ToJsonString() ?? "?"} items={property["items"]?.ToJsonString() ?? "[]"}",
            _ => property.ToJsonString()
        };
    }

    private static string FormatValueSummary(JsonObject property)
    {
        var value = property["value"]?.ToJsonString() ?? "null";
        var timestamp = property["timestamp"]?.GetValue<string>() ?? "never";
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
