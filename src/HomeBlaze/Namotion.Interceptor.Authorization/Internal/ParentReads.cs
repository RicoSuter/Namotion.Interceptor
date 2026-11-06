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
    // Up to this many reads a scan finds a subject's read faster than a lookup.
    private const int ScannedReads = 8;

    private readonly List<Read> _reads = [];
    private readonly Dictionary<RegisteredSubject, int> _readIndexes = new(ReferenceEqualityComparer.Instance);

    public int Capacity => _reads.Capacity;

    /// <summary>
    /// Reads and records the parents of the subject, then whether it is attached. A subject read before in this
    /// instance returns its recorded read, so the reads stay one per subject and agree with each other.
    /// </summary>
    public ImmutableArray<SubjectPropertyParent> GetParents(RegisteredSubject subject, out bool isAttached)
    {
        var index = IndexOf(subject);
        if (index >= 0)
        {
            var read = _reads[index];
            isAttached = read.IsAttached;
            return read.Parents;
        }

        // Parents first: the registry clears the attachment before it removes the last parent.
        var parents = subject.Parents;
        isAttached = subject.IsAttached;
        _reads.Add(new Read(subject, parents, isAttached));
        if (_reads.Count > ScannedReads)
        {
            if (_readIndexes.Count == 0)
            {
                for (var i = 0; i < _reads.Count; i++)
                {
                    _readIndexes.Add(_reads[i].Subject, i);
                }
            }
            else
            {
                _readIndexes.Add(subject, _reads.Count - 1);
            }
        }

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

    public void Clear()
    {
        if (_reads.Count > ScannedReads)
        {
            _readIndexes.Clear();
        }

        _reads.Clear();
    }

    private int IndexOf(RegisteredSubject subject)
    {
        if (_reads.Count > ScannedReads)
        {
            return _readIndexes.TryGetValue(subject, out var index) ? index : -1;
        }

        for (var i = 0; i < _reads.Count; i++)
        {
            if (ReferenceEquals(_reads[i].Subject, subject))
            {
                return i;
            }
        }

        return -1;
    }

    private readonly record struct Read(RegisteredSubject Subject, ImmutableArray<SubjectPropertyParent> Parents, bool IsAttached);
}
