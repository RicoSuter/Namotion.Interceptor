namespace Namotion.Interceptor.ConnectorTester.Performance;

/// <summary>
/// Computed percentile and statistical summary of one reporting window of latency samples.
/// Constructed from a SORTED ascending list of values. Empty input returns NaN.
/// </summary>
public sealed class PerformanceWindow
{
    public double P50 { get; }
    public double P90 { get; }
    public double P95 { get; }
    public double P99 { get; }
    public double P999 { get; }
    public double Max { get; }
    public double StdDev { get; }
    public int Count { get; }
    public double Mean { get; }

    private PerformanceWindow(double p50, double p90, double p95, double p99, double p999, double max, double stdDev, int count, double mean)
    {
        P50 = p50; P90 = p90; P95 = p95; P99 = p99; P999 = p999;
        Max = max; StdDev = stdDev; Count = count; Mean = mean;
    }

    public static PerformanceWindow FromSorted(IReadOnlyList<double> sortedAsc)
    {
        if (sortedAsc.Count == 0)
        {
            return new PerformanceWindow(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0, double.NaN);
        }

        var mean = sortedAsc.Average();
        var stdDev = ComputeStdDev(sortedAsc, mean);
        return new PerformanceWindow(
            Percentile(sortedAsc, 0.50),
            Percentile(sortedAsc, 0.90),
            Percentile(sortedAsc, 0.95),
            Percentile(sortedAsc, 0.99),
            Percentile(sortedAsc, 0.999),
            sortedAsc[^1],
            stdDev,
            sortedAsc.Count,
            mean);
    }

    public static double Percentile(IReadOnlyList<double> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return double.NaN;
        var index = (int)Math.Ceiling(sortedAsc.Count * p) - 1;
        return sortedAsc[Math.Clamp(index, 0, sortedAsc.Count - 1)];
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
