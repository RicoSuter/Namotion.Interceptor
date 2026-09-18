using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builder for creating a SubjectUpdate. Tracks IDs, subjects, and transformations.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateBuilder
{
    private Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> _subjects = new();

    private readonly Dictionary<IInterceptorSubject, string> _subjectToId = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<
        SubjectPropertyUpdate, (RegisteredSubjectProperty Property,
        IDictionary<string, SubjectPropertyUpdate> Parent)> _propertyUpdates = new();

    private readonly HashSet<IInterceptorSubject> _completeSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RegisteredSubject> _rootedSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RegisteredSubject> _unrootedSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RegisteredSubject> _reachableSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RegisteredSubject> _unreachableSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RegisteredSubject> _visitedSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<RegisteredSubject> _pendingSubjects = new();

    private readonly HashSet<PropertyReference> _changedProperties = new(PropertyReference.Comparer);
    private ChangeMerger? _changeMerger;
    private HashSet<string>? _completeSubjectIds;

    public ISubjectUpdateProcessor[] Processors { get; private set; } = [];

    public IInterceptorSubject RootSubject { get; private set; } = null!;

    public bool IsPartialUpdate { get; private set; }

    /// <summary>
    /// The value changes of a partial update, held back until its structural changes are built, with the
    /// index of each change and its registered property.
    /// </summary>
    public List<(int Index, RegisteredSubjectProperty Property)> ValueChanges { get; } = [];

    /// <summary>
    /// The members of the previous value of the collection change being built. Only a change diffs against
    /// a previous value, and the builds it nests to complete new members never do, so one set serves all.
    /// </summary>
    public HashSet<IInterceptorSubject> PreviousItems { get; } = new(ReferenceEqualityComparer.Instance);

    public void Initialize(IInterceptorSubject rootSubject, ISubjectUpdateProcessor[] processors, bool isPartialUpdate)
    {
        Processors = processors;
        RootSubject = rootSubject;
        IsPartialUpdate = isPartialUpdate;
        if (isPartialUpdate)
        {
            // A partial update always carries the set, even when it stays empty. An absent set means
            // "every referenced subject is complete", which would licence the receiver to fabricate
            // default-valued subjects for ids a reorder or a removal only references.
            _completeSubjectIds = [];
        }

        GetOrCreateId(rootSubject);
    }

    /// <summary>
    /// Collapses repeated changes of one property to a single change from its oldest previous value to its
    /// newest value, so that neither an intermediate value nor the arrival order reaches the update.
    /// </summary>
    public ReadOnlySpan<SubjectPropertyChange> MergeChanges(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        // A batch from a change queue is merged already, so only the check for a repeat is paid then.
        try
        {
            for (var i = 0; i < changes.Length; i++)
            {
                if (!_changedProperties.Add(changes[i].Property))
                    return (_changeMerger ??= new ChangeMerger()).Merge(changes).Span;
            }

            return changes;
        }
        finally
        {
            _changedProperties.Clear();
        }
    }

    public bool IsIncluded(RegisteredSubjectProperty property)
    {
        for (var i = 0; i < Processors.Length; i++)
        {
            if (!Processors[i].IsIncluded(property))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether an update can carry <paramref name="property"/>: it is no computed projection, and the processors
    /// include it and, for an attribute, the property it is attached to, whose update the attribute is part of.
    /// </summary>
    public bool IsPublished(RegisteredSubjectProperty property)
        => !SubjectUpdateFactory.IsComputedSubjectProjection(property) && IsIncludedWithAttributedProperties(property);

    /// <summary>
    /// Whether the processors include <paramref name="property"/> and, for an attribute, every property it is
    /// attached to. A property a subject is attached through is never a computed projection.
    /// </summary>
    private bool IsIncludedWithAttributedProperties(RegisteredSubjectProperty property)
    {
        while (IsIncluded(property))
        {
            if (!property.IsAttribute)
                return true;

            property = property.GetAttributedProperty();
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="subject"/> is reachable from the root through published properties, which a
    /// subject can fail to be while it is attached: behind an excluded property, outside the root's subtree,
    /// or in a cycle of references that nothing else references.
    /// </summary>
    public bool IsReachable(RegisteredSubject subject)
    {
        if (ReferenceEquals(subject.Subject, RootSubject) || _rootedSubjects.Contains(subject) ||
            _reachableSubjects.Contains(subject) || IsRooted(subject))
        {
            return true;
        }

        return !_unreachableSubjects.Contains(subject) && IsReachableThroughAnyParents(subject);
    }

    /// <summary>
    /// Whether a complete payload stating <paramref name="item"/> at a position of <paramref name="property"/>
    /// has to complete it. In a partial update it does only where the item is placed canonically: at its first
    /// published parent, or anywhere when following first published parents from it does not lead to the root.
    /// A canonically placed item is known to the receiver, or is completed along that way wherever this batch
    /// added an edge of it.
    /// </summary>
    public bool IsExpandedThrough(IInterceptorSubject item, RegisteredSubjectProperty? property)
    {
        if (!IsPartialUpdate || item.TryGetRegisteredSubject() is not { } registeredItem)
            return true;

        var firstParent = GetFirstPublishedParent(registeredItem);
        return firstParent is null || ReferenceEquals(firstParent, property) || !IsRooted(registeredItem);
    }

    private RegisteredSubjectProperty? GetFirstPublishedParent(RegisteredSubject subject)
    {
        foreach (var parent in subject.Parents)
        {
            if (IsIncludedWithAttributedProperties(parent.Property))
                return parent.Property;
        }

        return null;
    }

    /// <summary>
    /// Whether following the first published parent of every subject from <paramref name="subject"/> upwards
    /// leads to the root, which is how a tree reaches its root. Every subject on the way shares the answer.
    /// </summary>
    private bool IsRooted(RegisteredSubject subject)
    {
        try
        {
            var current = subject;
            while (true)
            {
                if (ReferenceEquals(current.Subject, RootSubject) || _rootedSubjects.Contains(current))
                {
                    AddVisitedSubjects(_rootedSubjects);
                    return true;
                }

                if (_unrootedSubjects.Contains(current) || !_visitedSubjects.Add(current) ||
                    GetFirstPublishedParent(current) is not { } parent)
                {
                    AddVisitedSubjects(_unrootedSubjects);
                    return false;
                }

                current = parent.Parent;
            }
        }
        finally
        {
            _visitedSubjects.Clear();
        }
    }

    /// <summary>
    /// Searches every published parent upwards. A search that finds no way proves every subject it visited
    /// unreachable, while a way it finds is only proven for the subject asked about.
    /// </summary>
    private bool IsReachableThroughAnyParents(RegisteredSubject subject)
    {
        try
        {
            _visitedSubjects.Add(subject);
            _pendingSubjects.Push(subject);
            while (_pendingSubjects.TryPop(out var current))
            {
                foreach (var parent in current.Parents)
                {
                    if (!IsIncludedWithAttributedProperties(parent.Property))
                        continue;

                    var parentSubject = parent.Property.Parent;
                    if (ReferenceEquals(parentSubject.Subject, RootSubject) ||
                        _rootedSubjects.Contains(parentSubject) || _reachableSubjects.Contains(parentSubject))
                    {
                        _reachableSubjects.Add(subject);
                        return true;
                    }

                    if (!_unreachableSubjects.Contains(parentSubject) && _visitedSubjects.Add(parentSubject))
                        _pendingSubjects.Push(parentSubject);
                }
            }

            AddVisitedSubjects(_unreachableSubjects);
            return false;
        }
        finally
        {
            _pendingSubjects.Clear();
            _visitedSubjects.Clear();
        }
    }

    private void AddVisitedSubjects(HashSet<RegisteredSubject> subjects)
    {
        foreach (var subject in _visitedSubjects)
        {
            subjects.Add(subject);
        }
    }

    public bool IsComplete(IInterceptorSubject subject) => _completeSubjects.Contains(subject);

    /// <summary>
    /// Records that the update carries the complete state of <paramref name="subject"/> and returns its
    /// property updates, which the caller fills.
    /// </summary>
    public Dictionary<string, SubjectPropertyUpdate> MarkComplete(IInterceptorSubject subject)
    {
        _completeSubjects.Add(subject);

        var subjectId = GetOrCreateId(subject);
        _completeSubjectIds?.Add(subjectId);
        return GetOrCreateProperties(subjectId);
    }

    public string GetOrCreateId(IInterceptorSubject subject)
    {
        if (!_subjectToId.TryGetValue(subject, out var id))
        {
            id = subject.GetOrAddSubjectId();
            _subjectToId[subject] = id;
        }

        return id;
    }

    public Dictionary<string, SubjectPropertyUpdate> GetOrCreateProperties(string subjectId)
    {
        if (!_subjects.TryGetValue(subjectId, out var properties))
        {
            properties = new Dictionary<string, SubjectPropertyUpdate>();
            _subjects[subjectId] = properties;
        }

        return properties;
    }

    public void TrackPropertyUpdate(
        SubjectPropertyUpdate update,
        RegisteredSubjectProperty property,
        IDictionary<string, SubjectPropertyUpdate> parent)
    {
        if (Processors.Length > 0)
        {
            _propertyUpdates[update] = (property, parent);
        }
    }

    /// <summary>
    /// Builds the final SubjectUpdate, applying all transformations.
    /// </summary>
    public SubjectUpdate Build(IInterceptorSubject subject)
    {
        ApplyTransformations();

        var rootId = GetOrCreateId(subject);

        // Build an ordered dictionary with root entry first for deterministic output.
        var orderedSubjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>();
        if (_subjects.TryGetValue(rootId, out var rootProps))
        {
            orderedSubjects[rootId] = rootProps;
        }
        foreach (var (key, value) in _subjects)
        {
            if (key != rootId)
            {
                orderedSubjects[key] = value;
            }
        }

        // Root is always included, also when the root subject has no property entries of its own: it
        // is what lets the receiver map the sender's root ID onto its own root subject, and a subject
        // that references the root (a parent pointer, for example) is otherwise unresolvable there.
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = orderedSubjects,
            CompleteSubjectIds = _completeSubjectIds
        };

        for (var i = 0; i < Processors.Length; i++)
        {
            update = Processors[i].TransformSubjectUpdate(subject, update);
        }

        return update;
    }

    /// <summary>
    /// Clears the builder for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _subjects = new(); // create a fresh dictionary, old one transferred to result
        _subjectToId.Clear();
        _propertyUpdates.Clear();
        _completeSubjects.Clear();
        _rootedSubjects.Clear();
        _unrootedSubjects.Clear();
        _reachableSubjects.Clear();
        _unreachableSubjects.Clear();
        _changeMerger?.Reset();
        _changedProperties.Clear();
        ValueChanges.Clear();
        PreviousItems.Clear();
        Processors = [];
        RootSubject = null!;
        IsPartialUpdate = false;
        _completeSubjectIds = null;
    }

    private void ApplyTransformations()
    {
        if (Processors.Length == 0)
            return;

        foreach (var (update, info) in _propertyUpdates)
        {
            // Key the update is stored under; a complete payload may have replaced a change's update since.
            var key = info.Property.IsAttribute
                ? info.Property.AttributeMetadata.AttributeName
                : info.Property.Name;

            if (!info.Parent.TryGetValue(key, out var current) || !ReferenceEquals(current, update))
                continue;

            for (var i = 0; i < Processors.Length; i++)
            {
                var transformed = Processors[i].TransformSubjectPropertyUpdate(info.Property, update);
                if (transformed != update)
                {
                    info.Parent[key] = transformed;
                }
            }
        }
    }
}
