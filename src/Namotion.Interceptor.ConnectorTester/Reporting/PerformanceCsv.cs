using Namotion.Interceptor.ConnectorTester.Performance;

namespace Namotion.Interceptor.ConnectorTester.Reporting;

/// <summary>
/// One row of performance-{participant}.csv: a single reporting interval snapshot for one
/// participant, including throughput, latency percentiles, and process resource usage.
/// </summary>
public sealed record PerformanceCsvRow(
    DateTimeOffset Timestamp,
    string Participant,
    int Cycle,
    double ReceivedPerSecond,
    double ReceivedAverage,
    double ReceivedP50,
    double ReceivedP90,
    double ReceivedP95,
    double ReceivedP99,
    double ReceivedP999,
    double ReceivedMax,
    double ReceivedProcessing,
    int Published,
    int Received,
    double CpuPercent,
    double ProcessMb,
    double HeapMb,
    double AllocationMbPerSec);

/// <summary>
/// Column schema and factory for the performance-{participant}.csv report. Widths and
/// formats are pinned to the historical output of <see cref="PerformanceProfiler"/> so
/// existing log scrapers and human readers see byte-identical lines after the refactor.
/// </summary>
public static class PerformanceCsv
{
    public static IReadOnlyList<CsvColumn<PerformanceCsvRow>> Columns { get; } =
    [
        new() { Name = "Timestamp",            Width = 24, Format = "yyyy-MM-ddTHH:mm:ss.fffZ", Selector = row => row.Timestamp },
        new() { Name = "Participant",          Width = 12,                                      Selector = row => row.Participant },
        new() { Name = "Cycle",                Width =  6,                                      Selector = row => row.Cycle },
        new() { Name = "Received/s",           Width = 12, Format = "F0",                       Selector = row => row.ReceivedPerSecond },
        new() { Name = "Received-Average",     Width = 16, Format = "F1",                       Selector = row => row.ReceivedAverage },
        new() { Name = "Received-P50",         Width = 14, Format = "F1",                       Selector = row => row.ReceivedP50 },
        new() { Name = "Received-P90",         Width = 14, Format = "F1",                       Selector = row => row.ReceivedP90 },
        new() { Name = "Received-P95",         Width = 14, Format = "F1",                       Selector = row => row.ReceivedP95 },
        new() { Name = "Received-P99",         Width = 14, Format = "F1",                       Selector = row => row.ReceivedP99 },
        new() { Name = "Received-P999",        Width = 15, Format = "F1",                       Selector = row => row.ReceivedP999 },
        new() { Name = "Received-Max",         Width = 14, Format = "F1",                       Selector = row => row.ReceivedMax },
        new() { Name = "Received-Processing",  Width = 20, Format = "F1",                       Selector = row => row.ReceivedProcessing },
        new() { Name = "Published",            Width = 12,                                      Selector = row => row.Published },
        new() { Name = "Received",             Width = 12,                                      Selector = row => row.Received },
        new() { Name = "CPU%",                 Width = 12, Format = "F1",                       Selector = row => row.CpuPercent },
        new() { Name = "ProcessMB",            Width = 12, Format = "F1",                       Selector = row => row.ProcessMb },
        new() { Name = "HeapMB",               Width = 12, Format = "F1",                       Selector = row => row.HeapMb },
        new() { Name = "AllocationMB/s",       Width = 14, Format = "F2",                       Selector = row => row.AllocationMbPerSec }
    ];

    public static CsvFile<PerformanceCsvRow> Create(string filePath) => new(filePath, Columns);
}
