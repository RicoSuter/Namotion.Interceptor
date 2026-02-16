using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text.Json;
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
    private const string MemoryLogPath = "logs/memory.log";

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly ConnectorTesterConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly List<(string Name, TestNode Root)> _participants;
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
        List<(string Name, TestNode Root)> participants,
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
        // Reset memory log for this run
        Directory.CreateDirectory(Path.GetDirectoryName(MemoryLogPath)!);
        if (File.Exists(MemoryLogPath))
        {
            File.Delete(MemoryLogPath);
        }

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
            string.Join(", ", _participants.Select(p => p.Name)));
        foreach (var engine in _mutationEngines)
        {
            _logger.LogInformation("  {Name}: {Rate} mutations/sec", engine.Name, engine.MutationRate);
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
                if (!_chaosEngines.Any(e => e.TargetName == participant))
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
                        participant.Name,
                        Snapshot: CreateSnapshot(participant.Root)))
                    .ToList();

                var firstSnapshot = snapshots[0].Snapshot;
                if (snapshots.All(snapshot => snapshot.Snapshot == firstSnapshot))
                {
                    convergeStopwatch.Stop();
                    cycleStopwatch.Stop();

                    WriteStatistics(cycleStopwatch.Elapsed, convergeStopwatch.Elapsed, "PASS");
                    AppendMemoryLog(activeProfileName, "PASS");
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
                        participant.Name,
                        Snapshot: CreateSnapshot(participant.Root)))
                    .ToList();

                _logger.LogError("=== Cycle {Cycle}: FAIL (did not converge within {Timeout}) ===",
                    _cycleNumber, _configuration.ConvergenceTimeout);

                // Log snapshot diffs
                var referenceSnapshot = snapshots[0];
                foreach (var snapshot in snapshots.Skip(1))
                {
                    if (snapshot.Snapshot != referenceSnapshot.Snapshot)
                    {
                        _logger.LogError("Mismatch between {Reference} and {Other}",
                            referenceSnapshot.Name, snapshot.Name);
                    }
                }

                // Log full snapshots
                foreach (var snapshot in snapshots)
                {
                    _logger.LogError("Snapshot [{Name}]: {Snapshot}", snapshot.Name, snapshot.Snapshot);
                }

                WriteStatistics(cycleStopwatch.Elapsed, convergeStopwatch.Elapsed, "FAIL");
                AppendMemoryLog(activeProfileName, "FAIL");
                _cycleLoggerProvider?.FinishCycle(_cycleNumber, false);

                _failed = true;
                _applicationLifetime.StopApplication();
                return;
            }
        }
    }

    private static string CreateSnapshot(TestNode root)
    {
        var update = SubjectUpdate.CreateCompleteUpdate(root, []);

        // Strip timestamps from structural properties (Collection, Dictionary, Object).
        // These are set during local graph creation and are inherently different per participant.
        // Value property timestamps ARE compared and must converge via source timestamps.
        if (update.Subjects != null)
        {
            foreach (var subject in update.Subjects.Values)
            {
                if (subject == null)
                {
                    continue;
                }

                foreach (var property in subject.Values)
                {
                    if (property.Kind != SubjectPropertyUpdateKind.Value)
                    {
                        property.Timestamp = null;
                    }
                }
            }
        }

        return JsonSerializer.Serialize(update, SnapshotJsonOptions);
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
            _logger.LogInformation("  {Name}: {Values:N0} value mutations",
                engine.Name, engine.ValueMutationCount);
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

    private void AppendMemoryLog(string? profileName, string result)
    {
        // Compact LOH to prevent fragmentation from large temporary objects
        // created during server restart cycles (NodeSet XML, serialization buffers).
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var process = Process.GetCurrentProcess();
        var workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0);
        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var profile = profileName != null ? $"profile: {profileName}" : "no profile";

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-ddTHH:mm:ss.fffZ}, Cycle {1}, {2}, {3}, ProcessMB: {4:F1}, HeapMB: {5:F1}",
            DateTimeOffset.UtcNow, _cycleNumber, profile, result, workingSetMb, heapMb);

        File.AppendAllText(MemoryLogPath, line + Environment.NewLine);
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
