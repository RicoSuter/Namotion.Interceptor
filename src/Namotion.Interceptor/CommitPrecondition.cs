using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

/// <summary>
/// Makes a write conditional on the property's commit revision. The terminal stores the value only when
/// the revision it reads under the subject lock is still <see cref="ExpectedCommitRevision"/>; otherwise
/// it leaves the value, the write state and <c>PropertyWriteContext.IsWritten</c> untouched, so the
/// chain publishes nothing and the caller sees the write as superseded. Which commits
/// the revision counts follows <see cref="PropertyReference.TryGetWriteState(bool, out long, out bool)"/>.
/// </summary>
internal readonly struct CommitPrecondition
{
    public readonly long ExpectedCommitRevision;
    public readonly bool IncludeSourceCommits;
    public readonly bool IsSet;

    public CommitPrecondition(long expectedCommitRevision, bool includeSourceCommits)
    {
        ExpectedCommitRevision = expectedCommitRevision;
        IncludeSourceCommits = includeSourceCommits;
        IsSet = true;
    }

    /// <summary>
    /// Evaluated by the terminal under the subject lock, which is what makes the check and the store
    /// atomic against other writes. Not inlined, so the terminal keeps a single branch for the
    /// unconditional path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool HoldsFor(in PropertyReference property)
    {
        property.TryGetWriteState(IncludeSourceCommits, out var commitRevision, out _);
        return commitRevision == ExpectedCommitRevision;
    }
}
