using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// One committed incoming graph edge. Property plus child-specific ordinal is its identity; index
/// is publication payload only and is never compared by lifecycle code.
/// </summary>
internal readonly struct IncomingEdge(PropertyReference property, int subjectOrdinal, object? index)
{
    public readonly PropertyReference Property = property;
    public readonly int SubjectOrdinal = subjectOrdinal;
    public readonly object? Index = index;
}

/// <summary>
/// The lifecycle's per-subject state: the occurrence-aware incoming edges that hold the subject in
/// the graph, and the parent snapshot published for <see cref="ParentsHandlerExtensions.GetParents"/>.
/// </summary>
/// <remarks>
/// The anchor is deliberately not mirrored here. It lives on the executor, which is the single
/// authority for it, so lifecycle state and attachment state cannot drift apart.
///
/// Locking: the lifecycle mutates the edges while holding its topology lock, so it is the only
/// writer. The instance itself is the monitor that additionally excludes a concurrent parent
/// materialization, which must not take the topology lock (see the deadlock note on
/// <see cref="OwnershipGraph.GetParents"/>). It is a leaf lock: nothing foreign is called while it
/// is held, and the type is internal and never handed out, so no other code can take it.
/// </remarks>
internal sealed class SubjectOwnership
{
    // The single-edge case stays inline: most subjects have exactly one parent, and a list would
    // double the per-subject footprint for them.
    private PropertyReference _firstProperty;
    private int _firstSubjectOrdinal;
    private object? _firstIndex;
    private List<IncomingEdge>? _additionalEdges;

    // Volatile because GetReferenceCount reads it lock-free from outside the per-subject monitor
    // (consumers call it from inside their own locks and from lifecycle callbacks, so it must not
    // take one).
    private volatile int _incomingCount;

    // Published parent snapshot, or null while parents were never asked for on this subject. Read
    // without any lock by GetParents; written only under this instance's monitor.
    private volatile SubjectParent[]? _publishedParents;

    /// <summary>
    /// Set by the first <see cref="ParentsHandlerExtensions.GetParents"/> call on this subject.
    /// From then on every edge change republishes the snapshot; a subject nobody asks about never
    /// allocates one.
    /// </summary>
    /// <remarks>
    /// Volatile because <see cref="RepublishParents"/> reads it outside this instance's monitor. A
    /// missed activation would not merely delay a snapshot, it would freeze one:
    /// <see cref="TryGetPublishedParents"/> never re-materializes once an array is published.
    /// </remarks>
    private volatile bool _areParentsActivated;

    private bool AreParentsActivated => _areParentsActivated;

    /// <summary>The number of committed incoming edge occurrences, which is the reference count.</summary>
    public int IncomingCount => _incomingCount;

    public void AddIncoming(PropertyReference property, int subjectOrdinal, object? index)
    {
        lock (this)
        {
            if (_incomingCount == 0)
            {
                _firstProperty = property;
                _firstSubjectOrdinal = subjectOrdinal;
                _firstIndex = index;
            }
            else
            {
                _additionalEdges ??= [];
                _additionalEdges.Add(new IncomingEdge(property, subjectOrdinal, index));
            }

            _incomingCount++;
        }
    }

    /// <summary>
    /// Removes the edge identified by parent property and child-specific ordinal.
    /// </summary>
    public bool RemoveIncoming(PropertyReference property, int subjectOrdinal)
    {
        lock (this)
        {
            if (_incomingCount == 0)
            {
                return false;
            }

            if (_firstProperty.Equals(property) && _firstSubjectOrdinal == subjectOrdinal)
            {
                RemoveFirstSlot();
                return true;
            }

            if (_additionalEdges is not null)
            {
                for (var i = 0; i < _additionalEdges.Count; i++)
                {
                    var edge = _additionalEdges[i];
                    if (!edge.Property.Equals(property) || edge.SubjectOrdinal != subjectOrdinal)
                    {
                        continue;
                    }

                    RemoveAdditionalAt(i);
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Replaces every payload index of one child property atomically by ordinal.</summary>
    public void UpdateIncomingIndices(PropertyReference property, IReadOnlyList<object?> indices)
    {
        lock (this)
        {
            if (_incomingCount > 0 && _firstProperty.Equals(property) && _firstSubjectOrdinal < indices.Count)
            {
                _firstIndex = indices[_firstSubjectOrdinal];
            }

            if (_additionalEdges is not null)
            {
                for (var i = 0; i < _additionalEdges.Count; i++)
                {
                    var edge = _additionalEdges[i];
                    if (edge.Property.Equals(property) && edge.SubjectOrdinal < indices.Count)
                    {
                        _additionalEdges[i] = new IncomingEdge(property, edge.SubjectOrdinal, indices[edge.SubjectOrdinal]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reads the single committed incoming edge of a subject that has exactly one, and reports false
    /// for every other count. Lets a caller that only needs the one-edge case read it without
    /// copying the edges out.
    /// </summary>
    public bool TryGetSingleIncoming(out IncomingEdge edge)
    {
        lock (this)
        {
            if (_incomingCount != 1)
            {
                edge = default;
                return false;
            }

            edge = new IncomingEdge(_firstProperty, _firstSubjectOrdinal, _firstIndex);
            return true;
        }
    }

    /// <summary>
    /// Copies every committed incoming edge into the target list, so a caller can drain them
    /// without holding this monitor while it publishes callbacks.
    /// </summary>
    public void CopyIncomingEdges(List<IncomingEdge> target)
    {
        lock (this)
        {
            if (_incomingCount == 0)
            {
                return;
            }

            target.Add(new IncomingEdge(_firstProperty, _firstSubjectOrdinal, _firstIndex));
            if (_additionalEdges is not null)
            {
                target.AddRange(_additionalEdges);
            }
        }
    }

    /// <summary>
    /// Reads the published parent snapshot without any lock. Returns false while parents were never
    /// asked for on this subject, which is what makes the projection lazily activated.
    /// </summary>
    public bool TryGetPublishedParents(out ImmutableArray<SubjectParent> parents)
    {
        var published = _publishedParents;
        parents = published is null ? [] : ImmutableCollectionsMarshal.AsImmutableArray(published);
        return published is not null;
    }

    /// <summary>
    /// Activates parent publication for this subject and materializes the current snapshot. Called
    /// by the first <see cref="ParentsHandlerExtensions.GetParents"/>, from any thread and without
    /// the topology lock.
    /// </summary>
    public ImmutableArray<SubjectParent> ActivateParents()
    {
        lock (this)
        {
            _areParentsActivated = true;
            return PublishParentsCore();
        }
    }

    /// <summary>
    /// Republishes the snapshot after an edge change. A subject nobody ever asked about pays one
    /// volatile read here and allocates nothing; the flag is re-tested under the monitor because a
    /// first <see cref="ActivateParents"/> can land between the two.
    /// </summary>
    public void RepublishParents()
    {
        if (!AreParentsActivated)
        {
            return;
        }

        lock (this)
        {
            if (!AreParentsActivated)
            {
                return;
            }

            PublishParentsCore();
        }
    }

    private ImmutableArray<SubjectParent> PublishParentsCore()
    {
        if (_incomingCount == 0)
        {
            _publishedParents = [];
            return [];
        }

        var parents = new SubjectParent[_incomingCount];
        parents[0] = new SubjectParent(_firstProperty, _firstIndex);
        if (_additionalEdges is not null)
        {
            for (var i = 0; i < _additionalEdges.Count; i++)
            {
                parents[i + 1] = new SubjectParent(_additionalEdges[i].Property, _additionalEdges[i].Index);
            }
        }

        _publishedParents = parents;
        return ImmutableCollectionsMarshal.AsImmutableArray(parents);
    }

    private void RemoveFirstSlot()
    {
        if (_additionalEdges is { Count: > 0 })
        {
            var promoted = _additionalEdges[0];
            _additionalEdges.RemoveAt(0);
            _firstProperty = promoted.Property;
            _firstSubjectOrdinal = promoted.SubjectOrdinal;
            _firstIndex = promoted.Index;
        }
        else
        {
            _firstProperty = default;
            _firstSubjectOrdinal = 0;
            _firstIndex = null;
        }

        _incomingCount--;
    }

    private void RemoveAdditionalAt(int index)
    {
        _additionalEdges!.RemoveAt(index);
        _incomingCount--;
    }
}
