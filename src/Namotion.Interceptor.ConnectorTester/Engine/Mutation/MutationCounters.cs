namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Atomic value-mutation and structural-mutation counters for one MutationEngine.
/// VerificationEngine reads these per cycle to write statistics.
/// </summary>
public sealed class MutationCounters
{
    private long _value;
    private long _structural;

    public long ValueMutationCount => Interlocked.Read(ref _value);
    public long StructuralMutationCount => Interlocked.Read(ref _structural);

    public void IncrementValue() => Interlocked.Increment(ref _value);
    public void IncrementStructural() => Interlocked.Increment(ref _structural);

    public void Reset()
    {
        Interlocked.Exchange(ref _value, 0);
        Interlocked.Exchange(ref _structural, 0);
    }
}
