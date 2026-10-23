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
    private long _conflictCommits;
    private long _sourceWriteFailedCommits;
    private long _unexpectedFailedCommits;

    public long ValueMutationCount => Interlocked.Read(ref _value);
    public long StructuralMutationCount => Interlocked.Read(ref _structural);

    /// <summary>
    /// Gets how many transaction commits were attempted, failed or not.
    /// </summary>
    public long CommitCount => Interlocked.Read(ref _commits);

    /// <summary>
    /// Gets how many commits failed with a <see cref="CommitFailureKind.Conflict"/>. Expected under chaos.
    /// </summary>
    public long ConflictCommitCount => Interlocked.Read(ref _conflictCommits);

    /// <summary>
    /// Gets how many commits failed with a <see cref="CommitFailureKind.SourceWriteFailure"/>. Expected under chaos:
    /// the mutations already counted via <see cref="IncrementValue"/> are applied locally but not on the failed source.
    /// </summary>
    public long SourceWriteFailedCommitCount => Interlocked.Read(ref _sourceWriteFailedCommits);

    /// <summary>
    /// Gets how many commits failed with a <see cref="CommitFailureKind.Unexpected"/> failure. Any fails the cycle.
    /// </summary>
    public long UnexpectedFailedCommitCount => Interlocked.Read(ref _unexpectedFailedCommits);

    public void IncrementValue() => Interlocked.Increment(ref _value);
    public void IncrementStructural() => Interlocked.Increment(ref _structural);
    public void IncrementCommit() => Interlocked.Increment(ref _commits);

    public void IncrementFailedCommit(CommitFailureKind kind)
    {
        switch (kind)
        {
            case CommitFailureKind.Conflict:
                Interlocked.Increment(ref _conflictCommits);
                break;
            case CommitFailureKind.SourceWriteFailure:
                Interlocked.Increment(ref _sourceWriteFailedCommits);
                break;
            default:
                Interlocked.Increment(ref _unexpectedFailedCommits);
                break;
        }
    }

    /// <summary>
    /// Returns true the first time <paramref name="signature"/> is passed since the last <see cref="Reset"/>.
    /// </summary>
    public bool TryMarkFirstCommitFailure(string signature) => _loggedCommitFailures.TryAdd(signature, 0);

    public void Reset()
    {
        Interlocked.Exchange(ref _value, 0);
        Interlocked.Exchange(ref _structural, 0);
        Interlocked.Exchange(ref _commits, 0);
        Interlocked.Exchange(ref _conflictCommits, 0);
        Interlocked.Exchange(ref _sourceWriteFailedCommits, 0);
        Interlocked.Exchange(ref _unexpectedFailedCommits, 0);
        _loggedCommitFailures.Clear();
    }
}
