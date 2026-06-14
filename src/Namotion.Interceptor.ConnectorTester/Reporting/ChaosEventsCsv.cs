using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;

namespace Namotion.Interceptor.ConnectorTester.Reporting;

/// <summary>
/// One row of chaos-events.csv: a single fault injection event from a chaos engine.
/// </summary>
public sealed record ChaosEventCsvRow(
    DateTimeOffset Timestamp,
    int Cycle,
    string Participant,
    FaultType FaultType,
    double DurationSeconds);

/// <summary>
/// Column schema and factory for the chaos-events.csv report. Widths and formats are pinned
/// to the historical output of <see cref="VerificationEngine"/> so existing log scrapers and
/// human readers see byte-identical lines after the refactor.
/// </summary>
public static class ChaosEventsCsv
{
    public static IReadOnlyList<CsvColumn<ChaosEventCsvRow>> Columns { get; } =
    [
        new() { Name = "Timestamp",       Width = 24, Format = "yyyy-MM-ddTHH:mm:ss.fffZ", Selector = row => row.Timestamp },
        new() { Name = "Cycle",           Width =  6,                                      Selector = row => row.Cycle },
        new() { Name = "Participant",     Width = 16,                                      Selector = row => row.Participant },
        new() { Name = "FaultType",       Width = 12,                                      Selector = row => row.FaultType },
        new() { Name = "DurationSeconds", Width = 16, Format = "F1",                       Selector = row => row.DurationSeconds }
    ];

    public static CsvFile<ChaosEventCsvRow> Create(string filePath) => new(filePath, Columns);
}
