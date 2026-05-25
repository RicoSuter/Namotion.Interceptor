using Namotion.Interceptor.ConnectorTester.Engine.Chaos;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Reporting;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Aggregates and emits per-cycle statistics: human-readable log block, cycles.csv row,
/// and chaos-events.csv rows. Calls HeapSampler at the end of each cycle to record post-GC
/// heap size for memory-leak trend analysis.
/// </summary>
public sealed class CycleStatistics
{
    private readonly CsvFile<CycleCsvRow> _cyclesCsv;
    private readonly CsvFile<ChaosEventCsvRow> _chaosEventsCsv;
    private readonly HeapSampler _heapSampler;
    private readonly List<MutationEngine> _mutationEngines;
    private readonly List<ChaosEngine> _chaosEngines;
    private readonly TimeSpan _mutatePhaseDuration;
    private readonly ILogger _logger;

    public CycleStatistics(
        CsvFile<CycleCsvRow> cyclesCsv,
        CsvFile<ChaosEventCsvRow> chaosEventsCsv,
        HeapSampler heapSampler,
        List<MutationEngine> mutationEngines,
        List<ChaosEngine> chaosEngines,
        TimeSpan mutatePhaseDuration,
        ILogger logger)
    {
        _cyclesCsv = cyclesCsv;
        _chaosEventsCsv = chaosEventsCsv;
        _heapSampler = heapSampler;
        _mutationEngines = mutationEngines;
        _chaosEngines = chaosEngines;
        _mutatePhaseDuration = mutatePhaseDuration;
        _logger = logger;
    }

    public void RecordPass(int cycleNumber, TimeSpan cycleDuration, TimeSpan convergeDuration, string? profileName)
        => Record(cycleNumber, cycleDuration, convergeDuration, CycleResult.Pass, profileName);

    public void RecordFail(int cycleNumber, TimeSpan cycleDuration, TimeSpan convergeDuration, string? profileName)
        => Record(cycleNumber, cycleDuration, convergeDuration, CycleResult.Fail, profileName);

    private void Record(int cycleNumber, TimeSpan cycleDuration, TimeSpan convergeDuration, CycleResult result, string? profileName)
    {
        WriteStatistics(cycleNumber, cycleDuration, convergeDuration, result, profileName);
        AppendCsvRows(cycleNumber, cycleDuration, convergeDuration, result, profileName);
    }

    private void WriteStatistics(int cycleNumber, TimeSpan cycleDuration, TimeSpan convergeDuration, CycleResult result, string? profileName)
    {
        var totalMutations = _mutationEngines.Sum(engine => engine.ValueMutationCount);
        var totalChaos = _chaosEngines.Sum(engine => engine.ChaosEventCount);

        _logger.LogInformation("""
            --- Cycle {Cycle} Statistics ---
            Duration: {CycleDuration:F0}s (converged in {ConvergeDuration:F1}s)
            Chaos profile: {Profile}
            Total mutations: {TotalMutations:N0} | Total chaos events: {TotalChaos}
            Result: {Result}
            """,
            cycleNumber, cycleDuration.TotalSeconds, convergeDuration.TotalSeconds,
            profileName ?? "(none)", totalMutations, totalChaos, result);

        foreach (var engine in _mutationEngines)
        {
            _logger.LogInformation("  {Name}: {Values:N0} value mutations, {Structural:N0} structural mutations",
                engine.Name, engine.ValueMutationCount, engine.StructuralMutationCount);
        }

        foreach (var engine in _chaosEngines)
        {
            foreach (var record in engine.EventHistory)
            {
                _logger.LogInformation("  {Name}: {FaultType} at {Time:HH:mm:ss} ({Duration:F1}s)",
                    engine.TargetName, record.FaultType, record.DisruptedAt.LocalDateTime, record.Duration.TotalSeconds);
            }
        }
    }

    private void AppendCsvRows(int cycleNumber, TimeSpan cycleDuration, TimeSpan convergeDuration, CycleResult result, string? profileName)
    {
        var (heapMb, processMb) = _heapSampler.CompactAndSample();

        var totalValueMutations = _mutationEngines.Sum(engine => engine.ValueMutationCount);
        var totalStructuralMutations = _mutationEngines.Sum(engine => engine.StructuralMutationCount);
        var totalChaosEvents = _chaosEngines.Sum(engine => engine.ChaosEventCount);

        try
        {
            _cyclesCsv.AppendRow(new CycleCsvRow(
                Timestamp: DateTimeOffset.UtcNow,
                Cycle: cycleNumber,
                Result: result,
                Profile: profileName ?? "",
                MutateSeconds: _mutatePhaseDuration.TotalSeconds,
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
                        Cycle: cycleNumber,
                        Participant: engine.TargetName,
                        FaultType: record.FaultType,
                        DurationSeconds: record.Duration.TotalSeconds));
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to append to cycles.csv or chaos-events.csv");
        }
    }
}
