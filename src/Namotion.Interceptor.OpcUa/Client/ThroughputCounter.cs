namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class ThroughputCounter
{
    private const int WindowSeconds = 60;

    private readonly long[] _buckets = new long[WindowSeconds];
    private long _currentSecond;

    public void Add(int count)
    {
        var nowSecond = Environment.TickCount64 / 1000;
        var bucketIndex = (int)(nowSecond % WindowSeconds);

        var lastSecond = Interlocked.Read(ref _currentSecond);
        if (nowSecond != lastSecond)
        {
            if (Interlocked.CompareExchange(ref _currentSecond, nowSecond, lastSecond) == lastSecond)
            {
                ClearStaleBuckets(lastSecond, nowSecond);
            }
        }

        Interlocked.Add(ref _buckets[bucketIndex], count);
    }

    public double GetRate()
    {
        var nowSecond = Environment.TickCount64 / 1000;
        var lastSecond = Interlocked.Read(ref _currentSecond);

        if (lastSecond == 0 && Volatile.Read(ref _buckets[0]) == 0)
        {
            return 0.0;
        }

        if (nowSecond - lastSecond >= WindowSeconds)
        {
            return 0.0;
        }

        long total = 0;
        for (var i = 0; i < WindowSeconds; i++)
        {
            total += Interlocked.Read(ref _buckets[i]);
        }

        return (double)total / WindowSeconds;
    }

    private void ClearStaleBuckets(long fromSecond, long toSecond)
    {
        var clearCount = Math.Min(toSecond - fromSecond, WindowSeconds);
        for (long s = fromSecond + 1; s <= fromSecond + clearCount; s++)
        {
            var index = (int)(s % WindowSeconds);
            Interlocked.Exchange(ref _buckets[index], 0);
        }
    }
}
