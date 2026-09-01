using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry;

/// <summary>
/// Registers subjects and their property edges as they enter the object graph.
/// </summary>
/// <remarks>
/// Runs before <see cref="ContextInheritanceHandler"/>, which walks down into a newly attached
/// subtree, so a subject is registered before the descent reaches its children. That holds at every
/// level, so while attaching, any handler running at or behind this one finds every ancestor of a
/// subject already registered. Detach does not mirror that; see the design doc. Also ordered ahead
/// of <see cref="ParentTrackingHandler"/>, which fixes the order of
/// the two recorders instead of leaving it to registration order. See "Handler Order Around the
/// Descent" in docs/design/tracking-lifecycle.md.
/// </remarks>
[RunsBefore(typeof(ParentTrackingHandler), typeof(ContextInheritanceHandler))]
public class SubjectRegistry :
    ISubjectRegistry,
    ISubjectIdRegistry,
    ISubjectIdRegistryWriter,
    ILifecycleHandler,
    IPropertyLifecycleHandler,
    IPropertyRelationshipHandler
{
    private readonly Lock _relationshipReconciliationGate = new();
    private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _knownSubjects =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, IInterceptorSubject[]> _childrenByProperty =
        new(PropertyReference.Comparer);
    private readonly Dictionary<string, IInterceptorSubject> _subjectIdToSubject = new();
    private ImmutableDictionary<IInterceptorSubject, RegisteredSubject>? _knownSubjectsSnapshot;

    /// <inheritdoc />
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var snapshot = Volatile.Read(ref _knownSubjectsSnapshot);
            return snapshot ?? GetKnownSubjectsSlow();
        }
    }

    private ImmutableDictionary<IInterceptorSubject, RegisteredSubject> GetKnownSubjectsSlow()
    {
        lock (_knownSubjects)
        {
            var snapshot = _knownSubjectsSnapshot;
            if (snapshot is not null)
                return snapshot;

            snapshot = ImmutableDictionary.CreateRange(
                ReferenceEqualityComparer.Instance,
                _knownSubjects);
            Volatile.Write(ref _knownSubjectsSnapshot, snapshot);
            return snapshot;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            return _knownSubjects.GetValueOrDefault(subject);
        }
    }

    /// <inheritdoc />
    string ISubjectIdRegistryWriter.GetOrAddSubjectId(IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            var existing = subject.TryGetSubjectId();
            if (existing is not null)
                return existing;

            var id = SubjectRegistryExtensions.GenerateSubjectId();
            SubjectRegistryExtensions.HasSubjectIds = true;
            subject.Data[(null, SubjectRegistryExtensions.SubjectIdKey)] = id;

            // Only populate reverse index for attached subjects; the lifecycle
            // attach handler will register IDs from Data for unattached subjects.
            if (_knownSubjects.ContainsKey(subject))
            {
                _subjectIdToSubject[id] = subject;
            }

            return id;
        }
    }

    /// <inheritdoc />
    void ISubjectIdRegistryWriter.SetSubjectId(IInterceptorSubject subject, string id)
    {
        lock (_knownSubjects)
        {
            if (_subjectIdToSubject.TryGetValue(id, out var existing) && !ReferenceEquals(existing, subject))
            {
                throw new InvalidOperationException(
                    $"Subject ID '{id}' is already in use by a different subject.");
            }

            var oldId = subject.TryGetSubjectId();
            if (oldId is not null && oldId != id)
            {
                throw new InvalidOperationException(
                    $"Subject already has ID '{oldId}'; cannot reassign to '{id}'.");
            }

            SubjectRegistryExtensions.HasSubjectIds = true;
            subject.Data[(null, SubjectRegistryExtensions.SubjectIdKey)] = id;

            // Only populate reverse index for attached subjects; the lifecycle
            // attach handler will register IDs from Data for unattached subjects.
            if (_knownSubjects.ContainsKey(subject))
            {
                _subjectIdToSubject[id] = subject;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetSubjectById(string subjectId, out IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            return _subjectIdToSubject.TryGetValue(subjectId, out subject!);
        }
    }

    /// <inheritdoc />
    void ILifecycleHandler.HandleLifecycleChange(SubjectLifecycleChange change)
    {
        lock (_relationshipReconciliationGate)
        {
            HandleLifecycleChange(change);
        }
    }

    private void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        RegisteredSubject? registeredSubject = null;
        RegisteredSubjectProperty? registeredProperty = null;

        lock (_knownSubjects)
        {
            if (change.IsContextAttach || change.IsPropertyReferenceAdded)
            {
                if (!_knownSubjects.TryGetValue(change.Subject, out registeredSubject))
                {
                    registeredSubject = RegisterSubject(change.Subject);
                }

                if (change.IsContextAttach)
                {
                    RegisterPreassignedSubjectId(change.Subject);
                }

                if (change is { IsPropertyReferenceAdded: true, Property: { } property })
                {
                    if (!_knownSubjects.TryGetValue(property.Subject, out var registeredParent))
                    {
                        registeredParent = RegisterSubject(property.Subject);
                    }

                    registeredProperty = registeredParent.TryGetProperty(property.Name) ??
                        throw new InvalidOperationException($"Property '{property.Name}' not found.");
                }
            }
            else if (change.IsPropertyReferenceRemoved || change.IsContextDetach)
            {
                registeredSubject = _knownSubjects.GetValueOrDefault(change.Subject);
                if (change is { IsPropertyReferenceRemoved: true, Property: { } property })
                {
                    registeredProperty = _knownSubjects
                        .GetValueOrDefault(property.Subject)?
                        .TryGetProperty(property.Name);
                }

                if (change.IsContextDetach && registeredSubject is not null)
                {
                    _knownSubjects.Remove(change.Subject);
                    Volatile.Write(ref _knownSubjectsSnapshot, null);

                    if (_subjectIdToSubject.Count > 0)
                    {
                        var subjectId = change.Subject.TryGetSubjectId();
                        if (subjectId is not null)
                        {
                            _subjectIdToSubject.Remove(subjectId);
                        }
                    }
                }
            }
        }

        // Known-subject state is deliberately released before taking either relationship-view lock.
        if (change.IsPropertyReferenceAdded && registeredSubject is not null && registeredProperty is not null)
        {
            // Lifecycle handlers can observe a Registry added after the relationship-handler snapshot was
            // captured. Seed that provisional occurrence from the lifecycle metadata in this topology window.
            var relationship = change.Relationship ?? new SubjectPropertyRelationship(
                change.Property!.Value,
                change.Subject,
                change.Index);
            registeredProperty.AddChildRelationship(relationship);
            registeredSubject.AddParentRelationship(registeredProperty, relationship);
        }
        else if (change.IsPropertyReferenceRemoved && registeredProperty is not null)
        {
            registeredProperty.RemoveChildRelationships(change.Subject);
            registeredSubject?.RemoveParentGroup(registeredProperty);
        }
    }

    private void RegisterPreassignedSubjectId(IInterceptorSubject subject)
    {
        var subjectId = subject.TryGetSubjectId();
        if (subjectId is not null &&
            (!_subjectIdToSubject.TryGetValue(subjectId, out var existingSubject) ||
             ReferenceEquals(existingSubject, subject)))
        {
            _subjectIdToSubject[subjectId] = subject;
        }
    }

    public void ReconcileChildRelationships(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships)
    {
        lock (_relationshipReconciliationGate)
        {
            ReconcileChildRelationshipsCore(property, relationships);
        }
    }

    private void ReconcileChildRelationshipsCore(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships)
    {
        RegisteredSubjectProperty? registeredProperty;
        var resolvedRelationships = new List<SubjectPropertyRelationship>(relationships.Length);
        var groupIndexes = new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
        var groups = new List<ResolvedRelationshipGroup>();
        var removedParents = new List<RegisteredSubject>();

        lock (_knownSubjects)
        {
            registeredProperty = _knownSubjects
                .GetValueOrDefault(property.Subject)?
                .TryGetProperty(property.Name);
            if (registeredProperty is null)
            {
                _childrenByProperty.Remove(property);
                return;
            }

            foreach (var relationship in relationships)
            {
                if (!_knownSubjects.TryGetValue(relationship.Child, out var registeredChild))
                {
                    continue;
                }

                resolvedRelationships.Add(relationship);
                if (!groupIndexes.TryGetValue(relationship.Child, out var groupIndex))
                {
                    groupIndex = groups.Count;
                    groupIndexes.Add(relationship.Child, groupIndex);
                    groups.Add(new ResolvedRelationshipGroup(relationship.Child, registeredChild));
                }

                groups[groupIndex].Relationships.Add(relationship);
            }

            if (_childrenByProperty.TryGetValue(property, out var previousChildren))
            {
                foreach (var previousChild in previousChildren)
                {
                    if (!groupIndexes.ContainsKey(previousChild) &&
                        _knownSubjects.TryGetValue(previousChild, out var registeredChild))
                    {
                        removedParents.Add(registeredChild);
                    }
                }
            }
        }

        // Complete every allocation before mutating a view. Allocation failure is not recoverable, but this
        // keeps ordinary exceptions from leaving a partially replaced relationship generation.
        var outgoingRelationships = resolvedRelationships.ToImmutableArray();
        foreach (var group in groups)
        {
            group.Seal();
        }

        // Never nest relationship-view locks. The operation-level gate prevents another callback from
        // interleaving its outgoing replacement with these incoming group replacements.
        registeredProperty.ReplaceChildRelationships(outgoingRelationships);
        foreach (var removedParent in removedParents)
        {
            removedParent.RemoveParentGroup(registeredProperty);
        }

        foreach (var group in groups)
        {
            group.RegisteredChild.ReplaceParentGroup(registeredProperty, group.SealedRelationships);
        }

        if (groups.Count == 0)
        {
            _childrenByProperty.Remove(property);
        }
        else
        {
            var currentChildren = new IInterceptorSubject[groups.Count];
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                currentChildren[groupIndex] = groups[groupIndex].Child;
            }

            _childrenByProperty[property] = currentChildren;
        }
    }

    void IPropertyLifecycleHandler.AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = TryGetRegisteredProperty(change.Property);
        if (property is not null)
        {
            foreach (var attribute in property.ReflectionAttributes.OfType<ISubjectPropertyInitializer>())
            {
                attribute.InitializeProperty(property);
            }

            foreach (var initializer in change.Subject.Context.GetServices<ISubjectPropertyInitializer>())
            {
                initializer.InitializeProperty(property);
            }
        }
    }

    void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    private RegisteredSubject RegisterSubject(IInterceptorSubject subject)
    {
        var registeredSubject = new RegisteredSubject(subject);
        _knownSubjects[subject] = registeredSubject;
        Volatile.Write(ref _knownSubjectsSnapshot, null);
        return registeredSubject;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegisteredSubjectProperty? TryGetRegisteredProperty(PropertyReference property)
    {
        return TryGetRegisteredSubject(property.Subject)?.TryGetProperty(property.Name);
    }

    private sealed class ResolvedRelationshipGroup(
        IInterceptorSubject child,
        RegisteredSubject registeredChild)
    {
        public IInterceptorSubject Child { get; } = child;

        public RegisteredSubject RegisteredChild { get; } = registeredChild;

        public List<SubjectPropertyRelationship> Relationships { get; } = [];

        public ImmutableArray<SubjectPropertyRelationship> SealedRelationships { get; private set; }

        public void Seal() => SealedRelationships = Relationships.ToImmutableArray();
    }
}
