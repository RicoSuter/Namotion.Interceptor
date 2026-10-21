namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Atomic value-mutation and structural-mutation counters for one MutationEngine.
/// VerificationEngine reads these per cycle to write statistics.
/// </summary>
public sealed class MutationCounters
{
    private long _value;
    private long _structural;
    private long _failedCommits;

    public long ValueMutationCount => Interlocked.Read(ref _value);
    public long StructuralMutationCount => Interlocked.Read(ref _structural);

    /// <summary>
    /// Gets how many transaction commits failed with <see cref="Tracking.Transactions.SubjectTransactionException"/>.
    /// Under BestEffort this is an expected chaos outcome, not a strategy failure: the mutations
    /// already counted via <see cref="IncrementValue"/> may be partially unapplied on the source side.
    /// </summary>
    public long FailedCommitCount => Interlocked.Read(ref _failedCommits);

    public void IncrementValue() => Interlocked.Increment(ref _value);
    public void IncrementStructural() => Interlocked.Increment(ref _structural);
    public void IncrementFailedCommit() => Interlocked.Increment(ref _failedCommits);

    public void Reset()
    {
        Interlocked.Exchange(ref _value, 0);
        Interlocked.Exchange(ref _structural, 0);
        Interlocked.Exchange(ref _failedCommits, 0);
    }
}
