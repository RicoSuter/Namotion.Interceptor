using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor, IStructuralPropertyRefreshHandler
{
    private static readonly object SamePropertyExceptionKey = new();

    private readonly Dictionary<IInterceptorSubject, PropertyReferenceSet> _attachedSubjects =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IInterceptorSubject, AttachOperation> _attachingSubjects =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, ProcessedPropertyState> _processedProperties =
        new(PropertyReference.Comparer);

    [ThreadStatic]
    private static Dictionary<LifecycleInterceptor, HashSet<PropertyReference>>? _activeReconciliations;

    [ThreadStatic]
    private static Stack<List<ProcessedSubjectReference>>? _listPool;

    /// <summary>
    /// Raised when a subject is attached to the object graph.
    /// Handlers must be exception-free and fast (invoked inside lock).
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectAttached;

    /// <summary>
    /// Raised when a subject is about to be detached from the object graph.
    /// Fires BEFORE ILifecycleHandler.HandleLifecycleChange (symmetric with SubjectAttached which fires AFTER).
    /// At this point, the full object graph is still accessible.
    /// Handlers must be exception-free and fast (invoked inside lock).
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectDetaching;

    public void AttachSubjectToContext(IInterceptorSubject subject)
    {
        lock (_attachedSubjects)
        {
            if (_attachingSubjects.ContainsKey(subject))
            {
                return;
            }

            var operation = new AttachOperation();
            _attachingSubjects.Add(subject, operation);
            try
            {
                var stagedProperties = StageSubjectProperties(subject);
                if (!IsAttachActive(subject, operation))
                {
                    AbortAttach(subject, operation, stagedProperties);
                    return;
                }

                foreach (var stagedProperty in stagedProperties)
                {
                    foreach (var membership in stagedProperty.Reconciliation.MembershipRemovals)
                    {
                        DetachFromProperty(
                            membership.Subject,
                            subject.Context,
                            stagedProperty.Property,
                            membership.LastIndex,
                            membership.LastRelationship);

                        if (!IsAttachActive(subject, operation))
                        {
                            AbortAttach(subject, operation, stagedProperties);
                            return;
                        }
                    }

                    foreach (var membership in stagedProperty.Reconciliation.MembershipAdditions)
                    {
                        if (AttachToProperty(
                                membership.Subject,
                                subject.Context,
                                stagedProperty.Property,
                                membership.FirstIndex,
                                membership.FirstRelationship))
                        {
                            operation.AppliedAdditions ??= [];
                            operation.AppliedAdditions.Add(new AppliedMembership(
                                membership.Subject,
                                stagedProperty.Property,
                                membership.FirstIndex,
                                membership.FirstRelationship));
                        }

                        if (!IsAttachActive(subject, operation))
                        {
                            AbortAttach(subject, operation, stagedProperties);
                            return;
                        }
                    }

                    _processedProperties[stagedProperty.Property] = stagedProperty.Reconciliation.State;
                }

                if (!_attachedSubjects.ContainsKey(subject))
                {
                    AttachToContext(subject, subject.Context);
                }

                if (!IsAttachActive(subject, operation))
                {
                    AbortAttach(subject, operation, stagedProperties);
                    return;
                }

                DispatchInitialRelationshipGroups(stagedProperties);
            }
            finally
            {
                if (_attachingSubjects.TryGetValue(subject, out var activeOperation) &&
                    ReferenceEquals(activeOperation, operation))
                {
                    _attachingSubjects.Remove(subject);
                }
            }
        }
    }

    public void DetachSubjectFromContext(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                if (_attachingSubjects.TryGetValue(subject, out var attachingSubject))
                {
                    attachingSubject.IsCancelled = true;
                }

                if (!_attachedSubjects.ContainsKey(subject))
                {
                    return;
                }

                var detachedProperties = CaptureDetachedProperties(subject, collectedSubjects);

                foreach (var child in collectedSubjects)
                {
                    DetachFromProperty(
                        child.Subject,
                        subject.Context,
                        child.Property,
                        child.Index,
                        child.Relationship);
                }

                DetachFromContext(subject, subject.Context, detachedProperties);
            }
        }
        finally
        {
            ReturnList(collectedSubjects);
        }
    }

    /// <summary>
    /// Attaches a subject directly to a context (root subject, no property reference).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AttachToContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        var isFirstAttach = _attachedSubjects.TryAdd(subject, default);
        if (!isFirstAttach)
        {
            return;
        }

        var count = subject.GetReferenceCount();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = count,
            IsContextAttach = true
        };

        var properties = subject.Properties.Keys;
        InvokeAddedLifecycleHandlers(subject, context, change);
        if (!IsSubjectAttachTransitionActive(subject))
        {
            return;
        }

        SubjectAttached?.Invoke(change);
        if (!IsSubjectAttachTransitionActive(subject))
        {
            return;
        }

        foreach (var propertyName in properties)
        {
            subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
            if (!IsSubjectAttachTransitionActive(subject))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Attaches a subject via a property reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AttachToProperty(IInterceptorSubject subject, IInterceptorSubjectContext context,
        PropertyReference property, object? index, SubjectPropertyRelationship? relationship = null)
    {
        ref var set = ref CollectionsMarshal.GetValueRefOrAddDefault(_attachedSubjects, subject, out var existed);
        var isFirstAttach = !existed;
        if (!set.Add(property))
        {
            return false;
        }

        var count = subject.IncrementReferenceCount();
        var change = new SubjectLifecycleChange(
            subject,
            property,
            index,
            count,
            isFirstAttach,
            isPropertyReferenceAdded: true,
            isPropertyReferenceRemoved: false,
            isContextDetach: false,
            relationship);

        var properties = subject.Properties.Keys;
        InvokeAddedLifecycleHandlers(subject, context, change);
        if (!IsStructuralParentActive(property.Subject))
        {
            return true;
        }

        if (isFirstAttach)
        {
            SubjectAttached?.Invoke(change);
            if (!IsStructuralParentActive(property.Subject))
            {
                return true;
            }

            foreach (var propertyName in properties)
            {
                subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
                if (!IsStructuralParentActive(property.Subject))
                {
                    return true;
                }
            }
        }

        return true;
    }
    
    private static void InvokeAddedLifecycleHandlers(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectLifecycleChange change)
    {
        var array = context.GetServices<ILifecycleHandler>();
        for (var index = 0; index < array.Length; index++)
        {
            var handler = array[index];
            handler.HandleLifecycleChange(change);
        }

        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.HandleLifecycleChange(change);
        }
    }

    /// <summary>
    /// Detaches a subject from a context (root subject, no property reference).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DetachFromContext(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context,
        List<DetachedProperty> detachedProperties)
    {
        if (!_attachedSubjects.Remove(subject))
        {
            return;
        }
        
        foreach (var entry in subject.Properties)
        {
            var property = new PropertyReference(subject, entry.Key);
            subject.DetachSubjectProperty(property);
        }

        var count = subject.GetReferenceCount();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = count,
            IsContextDetach = true
        };

        SubjectDetaching?.Invoke(change);
        var firstException = ClearRelationshipsAndProcessedStates(detachedProperties);
        InvokeRemovedLifecycleHandlers(subject, context, change);

        firstException?.Throw();
    }

    /// <summary>
    /// Detaches a subject from a property reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DetachFromProperty(
        IInterceptorSubject subject, IInterceptorSubjectContext context,
        PropertyReference property, object? index, SubjectPropertyRelationship? relationship = null)
    {
        ref var set = ref CollectionsMarshal.GetValueRefOrNullRef(_attachedSubjects, subject);
        if (Unsafe.IsNullRef(ref set) || !set.Remove(property))
        {
            return;
        }

        var isLastDetach = set.IsEmpty;

        List<ProcessedSubjectReference>? children = null;
        List<DetachedProperty>? detachedProperties = null;
        if (isLastDetach)
        {
            _attachedSubjects.Remove(subject);
            children = GetList();
            detachedProperties = CaptureDetachedProperties(subject, children);

            foreach (var entry in subject.Properties)
            {
                subject.DetachSubjectProperty(new PropertyReference(subject, entry.Key));
            }
        }

        var count = subject.DecrementReferenceCount();
        var change = new SubjectLifecycleChange(
            subject,
            property,
            index,
            count,
            isContextAttach: false,
            isPropertyReferenceAdded: false,
            isPropertyReferenceRemoved: true,
            isLastDetach,
            relationship);

        if (isLastDetach)
        {
            SubjectDetaching?.Invoke(change);
        }

        var firstException = detachedProperties is not null
            ? ClearRelationshipsAndProcessedStates(detachedProperties)
            : null;
        InvokeRemovedLifecycleHandlers(subject, context, change);

        if (children is not null)
        {
            try
            {
                foreach (var child in children)
                {
                    DetachFromProperty(
                        child.Subject,
                        context,
                        child.Property,
                        child.Index,
                        child.Relationship);
                }
            }
            finally
            {
                ReturnList(children);
            }
        }

        firstException?.Throw();
    }

    private static void InvokeRemovedLifecycleHandlers(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectLifecycleChange change)
    {
        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.HandleLifecycleChange(change);
        }

        var array = context.GetServices<ILifecycleHandler>();
        for (var index = 0; index < array.Length; index++)
        {
            var handler = array[index];
            handler.HandleLifecycleChange(change);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Re-entrant writes to different properties are supported. A write to the property currently being
    /// reconciled throws before nested processing can corrupt its canonical baseline.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var metadata = context.Property.Metadata;
        if (!metadata.Type.CanContainSubjects<TProperty>())
        {
            next(ref context);
            return;
        }

        var relationshipHandlers = context.Property.Subject.Context.GetServices<IPropertyRelationshipHandler>();
        var materializeRelationships = relationshipHandlers.Length > 0 ||
                                       context.Property.Subject is IPropertyRelationshipHandler;

        var property = context.Property;
        EnterReconciliation(property);
        try
        {
            ExceptionDispatchInfo? downstreamReentry = null;
            try
            {
                next(ref context);
            }
            catch (InvalidOperationException exception)
                when (IsSamePropertyReconciliationException(exception, this, property) && context.IsWritten)
            {
                // A downstream lifecycle authority may already have committed the outer value before its
                // relationship callback attempts the nested write. Finish this authority's outer generation
                // as well, then preserve the guard failure for the caller.
                downstreamReentry = ExceptionDispatchInfo.Capture(exception);
            }

            if (context.IsWritten)
            {
                ReconcileStructuralProperty(
                    property,
                    metadata,
                    relationshipHandlers,
                    materializeRelationships,
                    context.NewValue,
                    useWrittenValueWhenGetterMissing: true);
            }

            downstreamReentry?.Throw();
        }
        finally
        {
            ExitReconciliation(property);
        }
    }

    void IStructuralPropertyRefreshHandler.RefreshStructuralProperty(PropertyReference property)
    {
        var metadata = property.Metadata;
        if (!metadata.Type.CanContainSubjects())
        {
            return;
        }

        var relationshipHandlers = property.Subject.Context.GetServices<IPropertyRelationshipHandler>();
        var materializeRelationships = relationshipHandlers.Length > 0 ||
                                       property.Subject is IPropertyRelationshipHandler;
        EnterReconciliation(property);
        try
        {
            ReconcileStructuralProperty(
                property,
                metadata,
                relationshipHandlers,
                materializeRelationships,
                writtenValue: null,
                useWrittenValueWhenGetterMissing: false);
        }
        finally
        {
            ExitReconciliation(property);
        }
    }

    private void ReconcileStructuralProperty(
        PropertyReference property,
        SubjectPropertyMetadata metadata,
        ImmutableArray<IPropertyRelationshipHandler> relationshipHandlers,
        bool materializeRelationships,
        object? writtenValue,
        bool useWrittenValueWhenGetterMissing)
    {
        lock (_attachedSubjects)
        {
            if (!_attachedSubjects.ContainsKey(property.Subject))
            {
                return;
            }

            // The terminal write runs outside this lock. Re-read the actual backing value after acquiring
            // the writer lock so racing writes converge to the latest value visible to this authority.
            // Setter-only generated properties have no getter, so their committed write value is authoritative.
            var value = metadata.GetValue is { } getValue
                ? getValue(property.Subject)
                : useWrittenValueWhenGetterMissing ? writtenValue : null;
            _processedProperties.TryGetValue(property, out var previousState);
            var reconciliation = SubjectPropertyRelationshipReconciler.Stage(
                property,
                value,
                previousState,
                materializeRelationships);
            List<AppliedMembership>? appliedAdditions = null;

            // Enumeration can run user code which re-enters lifecycle operations because Monitor is
            // re-entrant. Do not commit a staged generation for a parent which left this authority.
            if (!_attachedSubjects.ContainsKey(property.Subject))
            {
                AbortPropertyReconciliation(
                    property,
                    relationshipHandlers,
                    materializeRelationships,
                    appliedAdditions);
                return;
            }

            foreach (var membership in reconciliation.MembershipRemovals)
            {
                DetachFromProperty(
                    membership.Subject,
                    property.Subject.Context,
                    property,
                    membership.LastIndex,
                    membership.LastRelationship);

                if (!_attachedSubjects.ContainsKey(property.Subject))
                {
                    AbortPropertyReconciliation(
                        property,
                        relationshipHandlers,
                        materializeRelationships,
                        appliedAdditions);
                    return;
                }
            }

            foreach (var membership in reconciliation.MembershipAdditions)
            {
                if (AttachToProperty(
                        membership.Subject,
                        property.Subject.Context,
                        property,
                        membership.FirstIndex,
                        membership.FirstRelationship))
                {
                    appliedAdditions ??= [];
                    appliedAdditions.Add(new AppliedMembership(
                        membership.Subject,
                        property,
                        membership.FirstIndex,
                        membership.FirstRelationship));
                }

                if (!_attachedSubjects.ContainsKey(property.Subject))
                {
                    AbortPropertyReconciliation(
                        property,
                        relationshipHandlers,
                        materializeRelationships,
                        appliedAdditions);
                    return;
                }
            }

            _processedProperties[property] = reconciliation.State;

            if (metadata.Type.IsSubjectCollectionType() &&
                reconciliation.State.Memberships.Length > reconciliation.MembershipAdditions.Length)
            {
                var lifecycleHandlers = property.Subject.Context.GetServices<IPropertyLifecycleHandler>();
                for (var index = 0; index < lifecycleHandlers.Length; index++)
                {
                    lifecycleHandlers[index].RefreshCollectionProperty(property, value);

                    if (!_attachedSubjects.ContainsKey(property.Subject))
                    {
                        AbortPropertyReconciliation(
                            property,
                            relationshipHandlers,
                            materializeRelationships,
                            appliedAdditions);
                        return;
                    }
                }

                if (property.Subject is IPropertyLifecycleHandler subjectHandler)
                {
                    subjectHandler.RefreshCollectionProperty(property, value);

                    if (!_attachedSubjects.ContainsKey(property.Subject))
                    {
                        AbortPropertyReconciliation(
                            property,
                            relationshipHandlers,
                            materializeRelationships,
                            appliedAdditions);
                        return;
                    }
                }
            }

            if (materializeRelationships)
            {
                property.Subject.ReconcileChildRelationships(
                    relationshipHandlers,
                    property,
                    reconciliation.State.Relationships.AsSpan());

                if (!_attachedSubjects.ContainsKey(property.Subject))
                {
                    AbortPropertyReconciliation(
                        property,
                        relationshipHandlers,
                        materializeRelationships,
                        appliedAdditions);
                }
            }
        }
    }

    private void EnterReconciliation(PropertyReference property)
    {
        _activeReconciliations ??=
            new Dictionary<LifecycleInterceptor, HashSet<PropertyReference>>(ReferenceEqualityComparer.Instance);
        if (!_activeReconciliations.TryGetValue(this, out var properties))
        {
            properties = new HashSet<PropertyReference>(PropertyReference.Comparer);
            _activeReconciliations.Add(this, properties);
        }

        if (!properties.Add(property))
        {
            var exception = new InvalidOperationException(
                $"Property '{property.Name}' on '{property.Subject.GetType().Name}' is already being reconciled.");
            exception.Data[SamePropertyExceptionKey] = new SamePropertyReconciliationMarker(this, property);
            throw exception;
        }
    }

    private void ExitReconciliation(PropertyReference property)
    {
        var activeReconciliations = _activeReconciliations!;
        var properties = activeReconciliations[this];
        properties.Remove(property);
        if (properties.Count == 0)
        {
            activeReconciliations.Remove(this);
        }
    }

    private List<StagedProperty> StageSubjectProperties(IInterceptorSubject subject)
    {
        var relationshipHandlers = subject.Context.GetServices<IPropertyRelationshipHandler>();
        var materializeRelationships = relationshipHandlers.Length > 0 ||
                                       subject is IPropertyRelationshipHandler;
        var stagedProperties = new List<StagedProperty>();
        foreach (var property in subject.Properties)
        {
            var metadata = property.Value;
            if (!metadata.IsIntercepted ||
                !metadata.Type.CanContainSubjects())
            {
                continue;
            }

            var propertyReference = new PropertyReference(subject, property.Key);
            var value = metadata.GetValue?.Invoke(subject);
            _processedProperties.TryGetValue(propertyReference, out var previousState);
            var reconciliation = SubjectPropertyRelationshipReconciler.Stage(
                propertyReference,
                value,
                previousState,
                materializeRelationships);
            stagedProperties.Add(new StagedProperty(
                propertyReference,
                relationshipHandlers,
                materializeRelationships,
                reconciliation));
        }

        return stagedProperties;
    }

    private List<DetachedProperty> CaptureDetachedProperties(
        IInterceptorSubject subject,
        List<ProcessedSubjectReference> collectedSubjects)
    {
        var relationshipHandlers = subject.Context.GetServices<IPropertyRelationshipHandler>();
        var materializeRelationships = relationshipHandlers.Length > 0 ||
                                       subject is IPropertyRelationshipHandler;
        var detachedProperties = new List<DetachedProperty>();
        foreach (var property in subject.Properties)
        {
            var metadata = property.Value;
            if (!metadata.IsIntercepted ||
                !metadata.Type.CanContainSubjects())
            {
                continue;
            }

            var propertyReference = new PropertyReference(subject, property.Key);
            if (_processedProperties.TryGetValue(propertyReference, out var processedState))
            {
                AddMemberships(propertyReference, processedState, collectedSubjects);
                detachedProperties.Add(new DetachedProperty(
                    propertyReference,
                    relationshipHandlers,
                    materializeRelationships));
            }
        }

        return detachedProperties;
    }

    private bool IsAttachActive(IInterceptorSubject subject, AttachOperation operation)
    {
        if (_attachingSubjects.TryGetValue(subject, out var activeOperation) &&
            ReferenceEquals(activeOperation, operation))
        {
            return !operation.IsCancelled;
        }

        return _attachedSubjects.ContainsKey(subject);
    }

    private bool IsSubjectAttachTransitionActive(IInterceptorSubject subject)
    {
        return _attachedSubjects.ContainsKey(subject) &&
               IsStructuralParentActive(subject);
    }

    private bool IsStructuralParentActive(IInterceptorSubject subject)
    {
        if (_attachingSubjects.TryGetValue(subject, out var operation))
        {
            return !operation.IsCancelled;
        }

        return _attachedSubjects.ContainsKey(subject);
    }

    private void DispatchInitialRelationshipGroups(List<StagedProperty> stagedProperties)
    {
        ExceptionDispatchInfo? firstException = null;
        foreach (var stagedProperty in stagedProperties)
        {
            if (!stagedProperty.MaterializeRelationships ||
                !stagedProperty.Reconciliation.HasRelationshipGeneration)
            {
                continue;
            }

            try
            {
                stagedProperty.Property.Subject.ReconcileChildRelationships(
                    stagedProperty.RelationshipHandlers,
                    stagedProperty.Property,
                    stagedProperty.Reconciliation.State.Relationships.AsSpan());
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        firstException?.Throw();
    }

    private void AbortAttach(
        IInterceptorSubject subject,
        AttachOperation operation,
        List<StagedProperty> stagedProperties)
    {
        if (operation.AppliedAdditions is not null)
        {
            for (var index = operation.AppliedAdditions.Count - 1; index >= 0; index--)
            {
                var addition = operation.AppliedAdditions[index];
                DetachFromProperty(
                    addition.Subject,
                    subject.Context,
                    addition.Property,
                    addition.Index,
                    addition.Relationship);
            }
        }

        ExceptionDispatchInfo? firstException = null;
        foreach (var stagedProperty in stagedProperties)
        {
            try
            {
                if (stagedProperty.MaterializeRelationships)
                {
                    stagedProperty.Property.Subject.ReconcileChildRelationships(
                        stagedProperty.RelationshipHandlers,
                        stagedProperty.Property,
                        []);
                }
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                _processedProperties.Remove(stagedProperty.Property);
            }
        }

        firstException?.Throw();
    }

    private void AbortPropertyReconciliation(
        PropertyReference property,
        ImmutableArray<IPropertyRelationshipHandler> relationshipHandlers,
        bool materializeRelationships,
        List<AppliedMembership>? appliedAdditions)
    {
        if (appliedAdditions is not null)
        {
            for (var index = appliedAdditions.Count - 1; index >= 0; index--)
            {
                var addition = appliedAdditions[index];
                DetachFromProperty(
                    addition.Subject,
                    property.Subject.Context,
                    addition.Property,
                    addition.Index,
                    addition.Relationship);
            }
        }

        try
        {
            if (materializeRelationships)
            {
                property.Subject.ReconcileChildRelationships(
                    relationshipHandlers,
                    property,
                    []);
            }
        }
        finally
        {
            _processedProperties.Remove(property);
        }
    }

    private ExceptionDispatchInfo? ClearRelationshipsAndProcessedStates(
        List<DetachedProperty> detachedProperties)
    {
        ExceptionDispatchInfo? firstException = null;
        foreach (var detachedProperty in detachedProperties)
        {
            try
            {
                if (detachedProperty.MaterializeRelationships)
                {
                    detachedProperty.Property.Subject.ReconcileChildRelationships(
                        detachedProperty.RelationshipHandlers,
                        detachedProperty.Property,
                        []);
                }
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                _processedProperties.Remove(detachedProperty.Property);
            }
        }

        return firstException;
    }

    private static void AddMemberships(
        PropertyReference property,
        ProcessedPropertyState state,
        List<ProcessedSubjectReference> collectedSubjects)
    {
        foreach (var membership in state.Memberships)
        {
            collectedSubjects.Add(new ProcessedSubjectReference(
                membership.Subject,
                property,
                membership.FirstIndex,
                membership.FirstRelationship));
        }
    }

    #region  Performance

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<ProcessedSubjectReference> GetList()
    {
        _listPool ??= new Stack<List<ProcessedSubjectReference>>();
        return _listPool.Count > 0
            ? _listPool.Pop()
            : new List<ProcessedSubjectReference>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnList(List<ProcessedSubjectReference> list)
    {
        list.Clear();
        _listPool ??= new Stack<List<ProcessedSubjectReference>>();
        _listPool.Push(list);
    }

    #endregion

    private static bool IsSamePropertyReconciliationException(
        InvalidOperationException exception,
        LifecycleInterceptor authority,
        PropertyReference property)
    {
        return exception.Data[SamePropertyExceptionKey] is SamePropertyReconciliationMarker marker &&
               ReferenceEquals(marker.Authority, authority) &&
               PropertyReference.Comparer.Equals(marker.Property, property);
    }

    private sealed record SamePropertyReconciliationMarker(
        LifecycleInterceptor Authority,
        PropertyReference Property);

    private sealed class AttachOperation
    {
        public bool IsCancelled { get; set; }

        public List<AppliedMembership>? AppliedAdditions { get; set; }
    }

    private readonly record struct StagedProperty(
        PropertyReference Property,
        ImmutableArray<IPropertyRelationshipHandler> RelationshipHandlers,
        bool MaterializeRelationships,
        StagedPropertyReconciliation Reconciliation);

    private readonly record struct DetachedProperty(
        PropertyReference Property,
        ImmutableArray<IPropertyRelationshipHandler> RelationshipHandlers,
        bool MaterializeRelationships);

    private readonly record struct AppliedMembership(
        IInterceptorSubject Subject,
        PropertyReference Property,
        object? Index,
        SubjectPropertyRelationship? Relationship);

    private readonly record struct ProcessedSubjectReference(
        IInterceptorSubject Subject,
        PropertyReference Property,
        object? Index,
        SubjectPropertyRelationship? Relationship);
}
