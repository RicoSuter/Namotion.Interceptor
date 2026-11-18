using System.Diagnostics;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine.Chaos;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Reporting;
using Namotion.Interceptor.ConnectorTester.Snapshot;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Top-level orchestrator. Runs repeating mutate/converge cycles.
/// When a cycle fails to converge, has an unexpected commit failure or loses a write, exits the process with non-zero
/// exit code.
/// </summary>
public class VerificationEngine : BackgroundService
{
    private static readonly TimeSpan SnapshotPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SourceSynchronizationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SourceSynchronizationPollInterval = TimeSpan.FromSeconds(1);

    private readonly CsvFile<CycleCsvRow> _cyclesCsv;
    private readonly CsvFile<ChaosEventCsvRow> _chaosEventsCsv;

    private readonly ConnectorTesterConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly Dictionary<string, TestNode> _participants;
    private readonly List<MutationEngine> _mutationEngines;
    private readonly List<ChaosEngine> _chaosEngines;
    private readonly ChaosProfileRotator _chaosProfileRotator;
    private readonly ICycleRecorder? _cycleRecorder;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger _logger;
    private readonly FindingsLog _findingsLog;
    private readonly ConvergenceChecker _convergenceChecker;
    private readonly FailureDiagnostics _failureDiagnostics;
    private readonly CycleStatistics _cycleStatistics;

    private int _cycleNumber;
    private bool _failed;

    /// <summary>
    /// Whether the last cycle failed. Used to set the process exit code.
    /// </summary>
    public bool Failed => _failed;

    public VerificationEngine(
        ConnectorTesterConfiguration configuration,
        TestCycleCoordinator coordinator,
        Dictionary<string, TestNode> participants,
        List<MutationEngine> mutationEngines,
        List<ChaosEngine> chaosEngines,
        ICycleRecorder? cycleRecorder,
        IHostApplicationLifetime applicationLifetime,
        ILogger logger,
        string runDirectory)
    {
        _configuration = configuration;
        _coordinator = coordinator;
        _participants = participants;
        _mutationEngines = mutationEngines;
        _chaosEngines = chaosEngines;
        _chaosProfileRotator = new ChaosProfileRotator(configuration.ChaosProfiles, chaosEngines, logger);
        _cycleRecorder = cycleRecorder;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _cyclesCsv = CyclesCsv.Create(Path.Combine(runDirectory, "cycles.csv"));
        _chaosEventsCsv = ChaosEventsCsv.Create(Path.Combine(runDirectory, "chaos-events.csv"));
        _findingsLog = new FindingsLog(
            Path.Combine(runDirectory, "findings.log"),
            () => _cycleNumber,
            () => _chaosEngines.Any(engine => engine.ChaosEventCount > 0),
            logger);

        var captureFunctions = participants.ToDictionary(
            participant => participant.Key,
            participant => (Func<string>)(() => SnapshotComparer.Capture(participant.Value)));
        _convergenceChecker = new ConvergenceChecker(captureFunctions, _configuration.ConvergenceTimeout, SnapshotPollInterval);
        _failureDiagnostics = new FailureDiagnostics(runDirectory, participants, logger);
        _cycleStatistics = new CycleStatistics(
            _cyclesCsv, _chaosEventsCsv,
            new HeapSampler(),
            mutationEngines, chaosEngines,
            _configuration.MutatePhaseDuration,
            logger);
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

        LogStartupInformation();

        if (_configuration.VerifyWriteDurability)
        {
            await WaitForSynchronizedSourcesAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _cycleNumber++;
            _coordinator.SetCycle(_cycleNumber);

            foreach (var engine in _mutationEngines)
            {
                engine.ResetCounters();
                engine.ResetDurabilityLedger();
            }
            foreach (var engine in _chaosEngines)
                engine.ResetCounters();

            var activeProfileName = _chaosProfileRotator.ApplyForCycle(_cycleNumber);

            _cycleRecorder?.StartCycle(_cycleNumber);

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
                chaosEngine.RecoverActiveDisruption();
            }

            // OPC UA needs ~15-20s: server restart + port bind, client keep-alive
            // detection (up to 5s), reconnect handler (5s), session + subscription setup.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

            var outcome = await _convergenceChecker.WaitForConvergenceAsync(stoppingToken);
            cycleStopwatch.Stop();

            List<WriteDurabilityResult> durabilityResults = outcome.Converged
                ? _mutationEngines.Select(engine => engine.VerifyWriteDurability()).ToList()
                : [];
            LogInconclusiveDurability(durabilityResults);

            if (CycleVerdict.Decide(outcome.Converged, _mutationEngines, durabilityResults) == CycleResult.Pass)
            {
                _findingsLog.AppendIfAny(outcome.Snapshots, outcome.Elapsed, durabilityResults);
                _cycleStatistics.RecordPass(_cycleNumber, cycleStopwatch.Elapsed, outcome.Elapsed, activeProfileName);
                var (subjects, properties) = SnapshotComparer.CountSubjectsAndProperties(outcome.Snapshots[0].Snapshot);
                _logger.LogInformation(
                    "=== Cycle {Cycle}: PASS (converged in {ConvergeTime:F1}s, cycle {CycleTime:F0}s; verified {Subjects} subjects, {Properties} properties across {Participants} participants) ===",
                    _cycleNumber, outcome.Elapsed.TotalSeconds, cycleStopwatch.Elapsed.TotalSeconds,
                    subjects, properties, outcome.Snapshots.Count);

                _cycleRecorder?.FinishCycle(_cycleNumber, CycleResult.Pass);
                continue;
            }

            if (outcome.Converged)
            {
                var unexpectedFailedCommitCount = _mutationEngines.Sum(engine => engine.UnexpectedFailedCommitCount);
                if (unexpectedFailedCommitCount > 0)
                {
                    _logger.LogError("=== Cycle {Cycle}: FAIL ({Count} unexpected commit failures, see the logged errors) ===",
                        _cycleNumber, unexpectedFailedCommitCount);
                }

                var durabilityViolations = durabilityResults
                    .Where(result => result.IsFailure)
                    .SelectMany(result => result.Violations)
                    .ToList();
                if (durabilityViolations.Count > 0)
                {
                    _logger.LogError("=== Cycle {Cycle}: FAIL ({Count} write-durability violation(s)) ===",
                        _cycleNumber, durabilityViolations.Count);
                    foreach (var violation in durabilityViolations)
                    {
                        _logger.LogError("  {Violation}", violation);
                    }
                }
            }
            else
            {
                _logger.LogError("=== Cycle {Cycle}: FAIL (did not converge within {Timeout}) ===",
                    _cycleNumber, _configuration.ConvergenceTimeout);

                await _failureDiagnostics.RunAsync(_cycleNumber, outcome.Snapshots, stoppingToken);
            }

            _cycleStatistics.RecordFail(_cycleNumber, cycleStopwatch.Elapsed, outcome.Elapsed, activeProfileName);
            _cycleRecorder?.FinishCycle(_cycleNumber, CycleResult.Fail);

            _failed = true;
            _applicationLifetime.StopApplication();
            return;
        }
    }

    private async Task WaitForSynchronizedSourcesAsync(CancellationToken stoppingToken)
    {
        var unsynchronized = await SourceSynchronization.WaitAsync(
            _mutationEngines, SourceSynchronizationTimeout, SourceSynchronizationPollInterval, stoppingToken);
        if (unsynchronized.Count > 0)
        {
            _logger.LogWarning(
                "Sources of {Participants} are not synchronized after {Timeout}, so the write-durability check of cycle 1 may report writes they discarded before claiming their properties",
                string.Join(", ", unsynchronized), SourceSynchronizationTimeout);
        }
    }

    private void LogInconclusiveDurability(IEnumerable<WriteDurabilityResult> durabilityResults)
    {
        foreach (var result in durabilityResults.Where(result => result.IsInconclusive))
        {
            _logger.LogWarning(
                "Cycle {Cycle}: write durability of {Participant} is inconclusive: {ViolationCount} write(s) lost, but its source's full retry queue dropped {DroppedCount} write(s) in the cycle",
                _cycleNumber, result.Participant, result.Violations.Count, result.DroppedWriteCount);
            foreach (var violation in result.Violations)
            {
                _logger.LogWarning("  {Violation}", violation);
            }
        }
    }

    private void LogStartupInformation()
    {
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
            string.Join(", ", _participants.Select(participant => participant.Key)));

        foreach (var engine in _mutationEngines)
        {
            _logger.LogInformation("  {Name}: {Rate} value mutations/sec, {StructuralRate} structural mutations/sec",
                engine.Name, engine.ValueMutationRate, engine.StructuralMutationRate);
        }

        if (_configuration.VerifyWriteDurability)
        {
            _logger.LogInformation("  Write-durability oracle: enabled");
        }

        if (_configuration.ChaosProfiles.Count > 0)
        {
            _logger.LogInformation("  Chaos profiles ({Count}): {Profiles}",
                _configuration.ChaosProfiles.Count,
                string.Join(" -> ", _configuration.ChaosProfiles.Select(profile => profile.Name)));
        }
    }
}
