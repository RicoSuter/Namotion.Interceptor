using System.Collections.Concurrent;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Atomic mutation and commit counters for one MutationEngine.
/// VerificationEngine reads these per cycle to write statistics and to decide the cycle result.
/// </summary>
public sealed class MutationCounters
{
    private readonly ConcurrentDictionary<string, byte> _loggedCommitFailures = new();

    private long _value;
    private long _structural;
    private long _commits;
    private readonly long[] _failedCommits = new long[(int)CommitFailureKind.Unexpected + 1];

    public long ValueMutationCount => Interlocked.Read(ref _value);
    public long StructuralMutationCount => Interlocked.Read(ref _structural);

    /// <summary>
    /// Gets how many transaction commits were attempted, failed or not.
    /// </summary>
    public long CommitCount => Interlocked.Read(ref _commits);

    /// <summary>
    /// Gets how many commits failed with a <see cref="CommitFailureKind.Conflict"/>.
    /// </summary>
    public long ConflictCommitCount => Interlocked.Read(ref _failedCommits[(int)CommitFailureKind.Conflict]);

    /// <summary>
    /// Gets how many commits failed with a <see cref="CommitFailureKind.SourceWriteFailure"/>. The mutations already
    /// counted via <see cref="IncrementValue"/> whose source write failed are applied neither locally nor on the source.
    /// </summary>
    public long SourceWriteFailedCommitCount => Interlocked.Read(ref _failedCommits[(int)CommitFailureKind.SourceWriteFailure]);

    /// <summary>
    /// Gets how many commits failed with a <see cref="CommitFailureKind.Unexpected"/> failure.
    /// </summary>
    public long UnexpectedFailedCommitCount => Interlocked.Read(ref _failedCommits[(int)CommitFailureKind.Unexpected]);

    public void IncrementValue() => Interlocked.Increment(ref _value);
    public void IncrementStructural() => Interlocked.Increment(ref _structural);
    public void IncrementCommit() => Interlocked.Increment(ref _commits);

    public void IncrementFailedCommit(CommitFailureKind kind) => Interlocked.Increment(ref _failedCommits[(int)kind]);

    /// <summary>
    /// Returns true the first time <paramref name="signature"/> is passed since the last <see cref="Reset"/>.
    /// </summary>
    public bool TryMarkFirstCommitFailure(string signature) => _loggedCommitFailures.TryAdd(signature, 0);

    public void Reset()
    {
        Interlocked.Exchange(ref _value, 0);
        Interlocked.Exchange(ref _structural, 0);
        Interlocked.Exchange(ref _commits, 0);
        for (var index = 0; index < _failedCommits.Length; index++)
        {
            Interlocked.Exchange(ref _failedCommits[index], 0);
        }

        _loggedCommitFailures.Clear();
    }
}
