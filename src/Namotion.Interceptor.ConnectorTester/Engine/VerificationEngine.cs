using System.Diagnostics;
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
    private readonly ConnectorTesterConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly List<(string Name, TestNode Root)> _participants;
    private readonly List<MutationEngine> _mutationEngines;
    private readonly List<ChaosEngine> _chaosEngines;
    private readonly CycleLoggerProvider? _cycleLoggerProvider;
    private readonly ILogger _logger;

    private int _cycleNumber;

    public VerificationEngine(
        ConnectorTesterConfiguration configuration,
        TestCycleCoordinator coordinator,
        List<(string Name, TestNode Root)> participants,
        List<MutationEngine> mutationEngines,
        List<ChaosEngine> chaosEngines,
        CycleLoggerProvider? cycleLoggerProvider,
        ILogger logger)
    {
        _configuration = configuration;
        _coordinator = coordinator;
        _participants = participants;
        _mutationEngines = mutationEngines;
        _chaosEngines = chaosEngines;
        _cycleLoggerProvider = cycleLoggerProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        while (!stoppingToken.IsCancellationRequested)
        {
            _cycleNumber++;

            // Reset counters
            foreach (var engine in _mutationEngines)
                engine.ResetCounters();
            foreach (var engine in _chaosEngines)
                engine.ResetCounters();

            _cycleLoggerProvider?.StartNewCycle(_cycleNumber);

            var cycleStopwatch = Stopwatch.StartNew();

            // 1. Mutate phase
            _coordinator.Resume();
            _logger.LogInformation("=== Cycle {Cycle}: Mutate phase started ({Duration}) ===",
                _cycleNumber, _configuration.MutatePhaseDuration);

            await Task.Delay(_configuration.MutatePhaseDuration, stoppingToken);

            // 2. Transition to converge
            _coordinator.Pause();
            _logger.LogInformation("=== Cycle {Cycle}: Converge phase started ===", _cycleNumber);

            // Recover all active chaos disruptions
            foreach (var chaosEngine in _chaosEngines)
            {
                await chaosEngine.RecoverActiveDisruptionAsync(stoppingToken);
            }

            // Grace period for in-flight operations
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            // 3. Poll-compare snapshots
            var convergeStopwatch = Stopwatch.StartNew();
            var converged = false;
            var convergenceTimeoutSeconds = (int)_configuration.ConvergenceTimeout.TotalSeconds;

            for (var poll = 0; poll < convergenceTimeoutSeconds; poll++)
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
                    _logger.LogInformation(
                        "=== Cycle {Cycle}: PASS (converged in {ConvergeTime:F1}s, cycle {CycleTime:F0}s) ===",
                        _cycleNumber, convergeStopwatch.Elapsed.TotalSeconds, cycleStopwatch.Elapsed.TotalSeconds);

                    _cycleLoggerProvider?.FinishCycle(_cycleNumber, true);
                    converged = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
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
                _cycleLoggerProvider?.FinishCycle(_cycleNumber, false);

                Environment.Exit(1);
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

        return JsonSerializer.Serialize(update, new JsonSerializerOptions
        {
            WriteIndented = false
        });
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
    }
}
