namespace Namotion.Interceptor.ConnectorTester.Performance;

/// <summary>
/// Reservoir-sampling helper for percentile-friendly latency tracking.
/// Below capacity, every Add appends. At capacity, Add replaces a random slot
/// when the random index falls inside the reservoir, matching today's
/// PerformanceProfiler.ReservoirAdd behavior.
/// </summary>
public sealed class ReservoirSampler
{
    private readonly int _maxSamples;
    private readonly Random _random;

    public ReservoirSampler(int maxSamples)
        : this(maxSamples, new Random())
    {
    }

    public ReservoirSampler(int maxSamples, int seed)
        : this(maxSamples, new Random(seed))
    {
    }

    private ReservoirSampler(int maxSamples, Random random)
    {
        _maxSamples = maxSamples;
        _random = random;
    }

    public void Add(List<double> reservoir, double value, int totalSeen)
    {
        if (reservoir.Count < _maxSamples)
        {
            reservoir.Add(value);
            return;
        }

        var index = _random.Next(totalSeen);
        if (index < _maxSamples)
        {
            reservoir[index] = value;
        }
    }
}
