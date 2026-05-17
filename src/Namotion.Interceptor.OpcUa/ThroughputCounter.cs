namespace Namotion.Interceptor.OpcUa;

internal sealed class ThroughputCounter
{
    private const int WindowSeconds = 60;

    // Each element packs [epoch (high 32 bits) | count (low 32 bits)].
    private readonly long[] _buckets = new long[WindowSeconds];

    public void Add(int count)
    {
        var nowEpoch = (uint)(Environment.TickCount64 / 1000);
        var bucketIndex = (int)(nowEpoch % WindowSeconds);

        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _buckets[bucketIndex]);
            var epoch = (uint)(current >>> 32);
            var existingCount = (uint)(current & 0xFFFFFFFF);

            var newValue = epoch == nowEpoch
                ? Pack(nowEpoch, existingCount + (uint)count)
                : Pack(nowEpoch, (uint)count);

            if (Interlocked.CompareExchange(ref _buckets[bucketIndex], newValue, current) == current)
                return;

            spin.SpinOnce();
        }
    }

    public double CurrentRate
    {
        get
        {
            var nowEpoch = (uint)(Environment.TickCount64 / 1000);

            long total = 0;
            for (var i = 0; i < WindowSeconds; i++)
            {
                var packed = Interlocked.Read(ref _buckets[i]);
                var epoch = (uint)(packed >>> 32);
                var count = (uint)(packed & 0xFFFFFFFF);

                if (nowEpoch - epoch < WindowSeconds)
                {
                    total += count;
                }
            }

            return total == 0 ? 0.0 : (double)total / WindowSeconds;
        }
    }

    private static long Pack(uint epoch, uint count) => ((long)epoch << 32) | count;
}
