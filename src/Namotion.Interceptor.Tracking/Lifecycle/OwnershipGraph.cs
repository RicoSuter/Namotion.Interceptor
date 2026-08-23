using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The committed ownership state of one context: which subjects it owns, the last reconciled value
/// of every structural property, and the primitives that claim executors for this context or hand
/// them back.
/// </summary>
/// <remarks>
/// Committed outgoing edges are not stored separately. The property baselines are the outgoing
/// truth: a subject commits an edge to a child exactly when the baseline of one of its structural
/// properties still contains that child. One representation instead of two removes the whole class
/// of bugs where the two disagree, and it is what makes the release descent and the reachability
/// walk read the same relation.
///
/// The owned map is a <see cref="ConcurrentDictionary{TKey,TValue}"/> with exactly one writer (the
/// lifecycle, under its topology lock). It is concurrent for the readers:
/// <c>GetParents</c> and
/// <c>GetReferenceCount</c> must not take that lock, so they
/// need a lock-free way to find a subject's record.
/// </remarks>
internal sealed class OwnershipGraph(IInterceptorSubjectContext context)
{
    private readonly ConcurrentDictionary<IInterceptorSubject, SubjectOwnership> _owned = new();
    private readonly Dictionary<PropertyReference, object?> _baselines = new(PropertyReference.Comparer);

    public IInterceptorSubjectContext Context { get; } = context;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubjectOwnership? TryGetOwnership(IInterceptorSubject subject)
    {
        return _owned.TryGetValue(subject, out var ownership) ? ownership : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOwned(IInterceptorSubject subject)
    {
        return _owned.ContainsKey(subject);
    }

    public SubjectOwnership AddOwnership(IInterceptorSubject subject)
    {
        var ownership = new SubjectOwnership();
        _owned[subject] = ownership;
        return ownership;
    }

    public void RemoveOwnership(IInterceptorSubject subject)
    {
        _owned.TryRemove(subject, out _);
    }

    #region Property baselines, which are also the committed outgoing edges

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? GetBaseline(PropertyReference property)
    {
        return _baselines.GetValueOrDefault(property);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBaseline(PropertyReference property, object? value)
    {
        _baselines[property] = value;
    }

    /// <summary>
    /// Whether the parent still commits an outgoing edge to the target through the given property.
    /// Every algorithm that reads incoming edges validates candidates through this: a reconcile
    /// commits the new property value before it updates the incoming records, so a stored incoming
    /// edge can name a parent that no longer references the subject.
    /// </summary>
    public bool CommitsEdgeTo(PropertyReference property, IInterceptorSubject target)
    {
        return _baselines.TryGetValue(property, out var value) &&
               StructuralValueScanner.Contains(property, value, target);
    }

    /// <summary>
    /// Appends every committed outgoing occurrence of the subject, in property enumeration order and
    /// then value order. This is the order the release descent visits children in.
    /// </summary>
    public void CollectCommittedChildren(IInterceptorSubject subject, List<(PropertyReference Property, SubjectOccurrence Occurrence)> children)
    {
        foreach (var entry in subject.Properties)
        {
            var metadata = entry.Value;
            if (!metadata.IsIntercepted || !metadata.Type.CanContainSubjects())
            {
                continue;
            }

            var property = new PropertyReference(subject, entry.Key);
            if (!_baselines.TryGetValue(property, out var value) || value is null)
            {
                continue;
            }

            var occurrences = LifecycleScratch.RentOccurrenceList();
            try
            {
                StructuralValueScanner.CollectOccurrences(property, value, occurrences);
                foreach (var occurrence in occurrences)
                {
                    children.Add((property, occurrence));
                }
            }
            finally
            {
                LifecycleScratch.Return(occurrences);
            }
        }
    }

    /// <summary>
    /// Whether the subject's structural properties already carry committed baselines, which is what
    /// tells an attach whether the subject's own component still has to be discovered. Checking the
    /// baselines rather than a flag keeps one source of truth: seeding is exactly what writes them.
    /// </summary>
    public bool AreBaselinesSeeded(IInterceptorSubject subject)
    {
        foreach (var entry in subject.Properties)
        {
            var metadata = entry.Value;
            if (metadata is { IsIntercepted: true } && metadata.Type.CanContainSubjects())
            {
                return _baselines.ContainsKey(new PropertyReference(subject, entry.Key));
            }
        }

        return true;
    }

    /// <summary>Drops every structural baseline of the subject; called when it leaves the graph.</summary>
    public void RemoveBaselines(IInterceptorSubject subject)
    {
        foreach (var entry in subject.Properties)
        {
            var metadata = entry.Value;
            if (metadata is { IsIntercepted: true } && metadata.Type.CanContainSubjects())
            {
                _baselines.Remove(new PropertyReference(subject, entry.Key));
            }
        }
    }

    /// <summary>
    /// Seeds the baselines of the subject's structural properties from their current getter values
    /// and appends the direct occurrences those values contain.
    /// </summary>
    public void SeedBaselines(IInterceptorSubject subject, List<(PropertyReference Property, SubjectOccurrence Occurrence)> children)
    {
        foreach (var entry in subject.Properties)
        {
            var metadata = entry.Value;
            if (!metadata.IsIntercepted || !metadata.Type.CanContainSubjects())
            {
                continue;
            }

            var property = new PropertyReference(subject, entry.Key);
            var value = metadata.GetValue?.Invoke(subject);
            _baselines[property] = value;

            if (value is null)
            {
                continue;
            }

            var occurrences = LifecycleScratch.RentOccurrenceList();
            try
            {
                StructuralValueScanner.CollectOccurrences(property, value, occurrences);
                foreach (var occurrence in occurrences)
                {
                    children.Add((property, occurrence));
                }
            }
            finally
            {
                LifecycleScratch.Return(occurrences);
            }
        }
    }

    #endregion

    #region Anchors and executor claims

    /// <summary>
    /// Whether the subject carries a root anchor on this context. The anchor lives on the executor
    /// and is never mirrored into the graph state, so there is nothing to keep in sync.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAnchored(IInterceptorSubject subject)
    {
        var executor = subject.Executor;
        return executor.Anchor != SubjectAnchorKind.None && ReferenceEquals(executor.AttachedContext, Context);
    }

    /// <summary>
    /// Claims the subject for this context, or confirms an existing claim. Returns false when a
    /// competing context owns it, which is a lost race rather than a caller error and is answered by
    /// releasing this operation's own claims.
    /// </summary>
    public bool TryClaim(IInterceptorSubject subject, SubjectAnchorKind anchor)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var revision);
            if (attachedContext is not null)
            {
                if (!ReferenceEquals(attachedContext, Context))
                {
                    return false;
                }

                // Already ours: never weaken the anchor a previous claim or an explicit attach set.
                if (anchor == SubjectAnchorKind.None || currentAnchor == anchor ||
                    currentAnchor == SubjectAnchorKind.Explicit)
                {
                    return true;
                }
            }

            if (executor.TryUpdateAttachment(revision, Context, anchor, out _))
            {
                return true;
            }
        }
    }

    /// <summary>Hands the subject's executor back, which is what makes it unattached again.</summary>
    public void ReleaseClaim(IInterceptorSubject subject)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out _, out var revision);
            if (!ReferenceEquals(attachedContext, Context))
            {
                return;
            }

            if (executor.TryUpdateAttachment(revision, null, SubjectAnchorKind.None, out _))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Clears a provisional anchor, and only a provisional one: an explicit anchor that landed
    /// concurrently must survive rather than be degraded by an adoption.
    /// </summary>
    public void ClearProvisionalAnchor(IInterceptorSubject subject)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);
            if (!ReferenceEquals(attachedContext, Context) || anchor != SubjectAnchorKind.Provisional)
            {
                return;
            }

            if (executor.TryUpdateAttachment(revision, Context, SubjectAnchorKind.None, out _))
            {
                return;
            }
        }
    }

    /// <summary>Promotes the subject's anchor, used by an explicit attach on an inherited subject.</summary>
    public void SetAnchor(IInterceptorSubject subject, SubjectAnchorKind anchor)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var revision);
            if (!ReferenceEquals(attachedContext, Context) || currentAnchor == anchor)
            {
                return;
            }

            if (executor.TryUpdateAttachment(revision, Context, anchor, out _))
            {
                return;
            }
        }
    }

    #endregion

    #region Component discovery

    /// <summary>
    /// Walks the structural component the value opens up, validating every subject against this
    /// context and collecting the unattached ones so they can be claimed as one batch before
    /// anything is published.
    /// </summary>
    /// <remarks>
    /// The walk descends into unattached subjects only. An attached same-context subject was
    /// validated when it was attached and its own component is already owned, so there is nothing
    /// below it that could be foreign. That bound is what keeps the cost proportional to the newly
    /// arriving subgraph rather than to the graph.
    /// </remarks>
    public void DiscoverComponent(
        PropertyReference property,
        object? value,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> unattached)
    {
        var occurrences = LifecycleScratch.RentOccurrenceList();
        try
        {
            StructuralValueScanner.CollectOccurrences(property, value, occurrences);
            foreach (var occurrence in occurrences)
            {
                DiscoverComponent(occurrence.Subject, visited, unattached);
            }
        }
        finally
        {
            LifecycleScratch.Return(occurrences);
        }
    }

    /// <inheritdoc cref="DiscoverComponent(PropertyReference,object?,HashSet{IInterceptorSubject},List{IInterceptorSubject})"/>
    public void DiscoverComponent(
        IInterceptorSubject start,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> unattached)
    {
        var pending = LifecycleScratch.RentSubjectStack();
        try
        {
            pending.Push(start);
            while (pending.Count > 0)
            {
                var subject = pending.Pop();
                if (!visited.Add(subject))
                {
                    continue;
                }

                var attachedContext = subject.Executor.AttachedContext;
                if (attachedContext is not null)
                {
                    if (!ReferenceEquals(attachedContext, Context))
                    {
                        throw new InvalidOperationException(
                            $"The subject '{subject.GetType().Name}' is owned by a different context and cannot " +
                            "join this graph. Detach it from that context first.");
                    }

                    continue;
                }

                unattached.Add(subject);

                foreach (var entry in subject.Properties)
                {
                    var metadata = entry.Value;
                    if (!metadata.IsIntercepted || !metadata.Type.CanContainSubjects())
                    {
                        continue;
                    }

                    var childValue = metadata.GetValue?.Invoke(subject);
                    if (childValue is null)
                    {
                        continue;
                    }

                    var childProperty = new PropertyReference(subject, entry.Key);
                    var occurrences = LifecycleScratch.RentOccurrenceList();
                    try
                    {
                        StructuralValueScanner.CollectOccurrences(childProperty, childValue, occurrences);
                        foreach (var occurrence in occurrences)
                        {
                            pending.Push(occurrence.Subject);
                        }
                    }
                    finally
                    {
                        LifecycleScratch.Return(occurrences);
                    }
                }
            }
        }
        finally
        {
            LifecycleScratch.Return(pending);
        }
    }

    /// <summary>
    /// Claims every discovered unattached subject. A lost race releases the claims this call made
    /// and reports failure, so the caller can throw before touching the backing property.
    /// </summary>
    public bool TryClaimDiscovered(List<IInterceptorSubject> unattached, IInterceptorSubject? explicitRoot, SubjectAnchorKind rootAnchor)
    {
        for (var i = 0; i < unattached.Count; i++)
        {
            var subject = unattached[i];
            var anchor = ReferenceEquals(subject, explicitRoot) ? rootAnchor : SubjectAnchorKind.None;
            if (TryClaim(subject, anchor))
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                ReleaseClaim(unattached[j]);
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Hands back every claim that did not end up carrying ownership, which happens when the write
    /// was suppressed downstream or the authoritative getter returned a different graph than the
    /// one that was validated.
    /// </summary>
    public void ReleaseUnusedClaims(List<IInterceptorSubject> claimed)
    {
        foreach (var subject in claimed)
        {
            if (!IsOwned(subject) && !IsAnchored(subject))
            {
                ReleaseClaim(subject);
            }
        }
    }

    #endregion
}
