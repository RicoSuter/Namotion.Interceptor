using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// Records the parents and attachment of every subject an evaluation reads, so it can check afterwards that the
/// graph did not change under it. The registry replaces a subject's parents array on every change and never mutates or
/// reuses one, and an instance never becomes attached again, so when every recorded read still returns the same array
/// and attachment, all reads held at one instant: the moment between the last read and the first re-read. The empty
/// array is the one shared array, and a subject that loses its last parent is detached first, which the attachment
/// comparison catches.
/// </summary>
internal sealed class ParentReads
{
    // Larger lists are dropped on return, so a search that used the whole budget does not pin its list to the thread.
    private const int MaxCachedCapacity = 256;

    [ThreadStatic]
    private static ParentReads? _cached;

    private readonly List<Read> _reads = [];

    /// <summary>
    /// Gets an empty instance, the thread's cached one when it is free; a nested evaluation on the same thread, such as
    /// a tag or path provider that calls the policy, gets its own.
    /// </summary>
    public static ParentReads Rent()
    {
        var reads = _cached ?? new ParentReads();
        _cached = null;
        return reads;
    }

    /// <summary>Clears the instance and caches it for the thread's next evaluation; it must not be used afterwards.</summary>
    public static void Return(ParentReads reads)
    {
        if (reads._reads.Capacity > MaxCachedCapacity)
        {
            return;
        }

        reads.Clear();
        _cached = reads;
    }

    /// <summary>Reads and records the parents of the subject, then whether it is attached.</summary>
    public ImmutableArray<SubjectPropertyParent> GetParents(RegisteredSubject subject, out bool isAttached)
    {
        // Parents first: the registry clears the attachment before it removes the last parent.
        var parents = subject.Parents;
        isAttached = subject.IsAttached;
        _reads.Add(new Read(subject, parents, isAttached));
        return parents;
    }

    /// <summary>Whether every recorded subject still has the same parents array and attachment.</summary>
    public bool AreUnchanged()
    {
        foreach (var read in _reads)
        {
            // Parents first, as in GetParents: a re-read that sees an array published after a detach then sees the detach.
            if (read.Subject.Parents != read.Parents || read.Subject.IsAttached != read.IsAttached)
            {
                return false;
            }
        }

        return true;
    }

    public void Clear() => _reads.Clear();

    private readonly record struct Read(RegisteredSubject Subject, ImmutableArray<SubjectPropertyParent> Parents, bool IsAttached);
}
