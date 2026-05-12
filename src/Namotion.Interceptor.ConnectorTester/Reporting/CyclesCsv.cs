using Namotion.Interceptor.ConnectorTester.Engine.Verification;

namespace Namotion.Interceptor.ConnectorTester.Reporting;

/// <summary>
/// One row of cycles.csv: a single mutate/converge cycle outcome plus its memory snapshot.
/// </summary>
public sealed record CycleCsvRow(
    DateTimeOffset Timestamp,
    int Cycle,
    CycleResult Result,
    string Profile,
    double MutateSeconds,
    double ConvergeSeconds,
    double CycleSeconds,
    long ValueMutations,
    long StructuralMutations,
    long ChaosEvents,
    double HeapMb,
    double ProcessMb);

/// <summary>
/// Column schema and factory for the cycles.csv report. Widths and formats are pinned to
/// the historical output of <see cref="VerificationEngine"/> so existing log scrapers and
/// human readers see byte-identical lines after the refactor.
/// </summary>
public static class CyclesCsv
{
    public static IReadOnlyList<CsvColumn<CycleCsvRow>> Columns { get; } =
    [
        new() { Name = "Timestamp",           Width = 24, Format = "yyyy-MM-ddTHH:mm:ss.fffZ", Selector = row => row.Timestamp },
        new() { Name = "Cycle",               Width =  6,                                      Selector = row => row.Cycle },
        new() { Name = "Result",              Width =  6,                                      Selector = row => row.Result },
        new() { Name = "Profile",             Width = 20,                                      Selector = row => row.Profile },
        new() { Name = "MutateSeconds",       Width = 14, Format = "F1",                       Selector = row => row.MutateSeconds },
        new() { Name = "ConvergeSeconds",     Width = 16, Format = "F1",                       Selector = row => row.ConvergeSeconds },
        new() { Name = "CycleSeconds",        Width = 12, Format = "F1",                       Selector = row => row.CycleSeconds },
        new() { Name = "ValueMutations",      Width = 16,                                      Selector = row => row.ValueMutations },
        new() { Name = "StructuralMutations", Width = 20,                                      Selector = row => row.StructuralMutations },
        new() { Name = "ChaosEvents",         Width = 12,                                      Selector = row => row.ChaosEvents },
        new() { Name = "HeapMB",              Width = 10, Format = "F1",                       Selector = row => row.HeapMb },
        new() { Name = "ProcessMB",           Width = 10, Format = "F1",                       Selector = row => row.ProcessMb }
    ];

    public static CsvFile<CycleCsvRow> Create(string filePath) => new(filePath, Columns);
}
