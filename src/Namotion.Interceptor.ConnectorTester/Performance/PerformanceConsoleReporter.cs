namespace Namotion.Interceptor.ConnectorTester.Performance;

/// <summary>
/// Writes the human-readable performance report block to Console under a shared lock,
/// matching today's PerformanceProfiler.PrintAndLogStats output format.
/// </summary>
public sealed class PerformanceConsoleReporter
{
    private static readonly Lock ConsoleLock = new();

    private readonly string _participantName;

    public PerformanceConsoleReporter(string participantName)
    {
        _participantName = participantName;
    }

    public void Report(
        DateTimeOffset now,
        double elapsedSeconds,
        int receivedCount,
        int publishedCount,
        double cpuPercent,
        double workingSetMb,
        double heapMb,
        double allocRateMbPerSec,
        IReadOnlyList<double> sortedThroughput,
        IReadOnlyList<double> sortedReceivedLatencies, double avgReceivedLatency, double receivedLatencyMax,
        IReadOnlyList<double> sortedChangedLatencies, double avgChangedLatency, double changedLatencyMax)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 129));
            Console.WriteLine($"[{_participantName}] Performance Report - [{now:yyyy-MM-dd HH:mm:ss.fff}]");
            Console.WriteLine();
            Console.WriteLine($"Total received changes:          {receivedCount}");
            Console.WriteLine($"Total published changes:         {publishedCount}");
            var cpuCores = cpuPercent / 100.0 * Environment.ProcessorCount;
            Console.WriteLine($"Process CPU:                     {Math.Round(cpuPercent, 1)}% ({Math.Round(cpuCores, 1)} cores)");
            Console.WriteLine($"Process memory:                  {Math.Round(workingSetMb, 2)} MB ({Math.Round(heapMb, 2)} MB in .NET heap)");
            Console.WriteLine($"Avg allocations over last {elapsedSeconds}s:   {Math.Round(allocRateMbPerSec, 2)} MB/s");
            Console.WriteLine();

            Console.WriteLine($"{"Metric",-29} {"Avg",10} {"P50",10} {"P90",10} {"P95",10} {"P99",10} {"P99.9",10} {"Max",10} {"StdDev",10} {"Count",10}");
            Console.WriteLine(new string('-', 129));

            if (sortedThroughput.Count > 0)
            {
                PrintLine("Received changes/s", sortedThroughput);
            }

            if (sortedReceivedLatencies.Count > 0)
            {
                PrintLine("Received processing (ms)", sortedReceivedLatencies, avgReceivedLatency, receivedCount, receivedLatencyMax);
            }

            if (sortedChangedLatencies.Count > 0)
            {
                PrintLine("Received E2E latency (ms)", sortedChangedLatencies, avgChangedLatency, receivedCount, changedLatencyMax);
            }
        }
    }

    private static void PrintLine(string label, IReadOnlyList<double> sortedValues, double? exactAvg = null, int? exactCount = null, double? exactMax = null)
    {
        var avg = exactAvg ?? sortedValues.Average();
        var count = exactCount ?? sortedValues.Count;
        var max = exactMax ?? sortedValues[^1];
        // Note: stdDev is computed against `avg` (which may be the running average passed via exactAvg,
        // not sortedValues.Average()), preserving today's byte-identical output.
        var stdDev = ComputeStdDev(sortedValues, avg);
        Console.WriteLine($"{label,-29} {avg,10:F2} {PerformanceWindow.Percentile(sortedValues, 0.50),10:F2} {PerformanceWindow.Percentile(sortedValues, 0.90),10:F2} {PerformanceWindow.Percentile(sortedValues, 0.95),10:F2} {PerformanceWindow.Percentile(sortedValues, 0.99),10:F2} {PerformanceWindow.Percentile(sortedValues, 0.999),10:F2} {max,10:F2} {stdDev,10:F2} {count,10}");
    }

    private static double ComputeStdDev(IReadOnlyList<double> values, double mean)
    {
        if (values.Count == 0) return double.NaN;
        double sumSquares = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var d = values[i] - mean;
            sumSquares += d * d;
        }
        return Math.Sqrt(sumSquares / values.Count);
    }
}
