using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor, IStructuralPropertyRefreshHandler
{
    private readonly Dictionary<IInterceptorSubject, PropertyReferenceSet> _attachedSubjects =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, ProcessedPropertyState> _processedProperties =
        new(PropertyReference.Comparer);
    private readonly HashSet<PropertyReference> _reconcilingProperties = new(PropertyReference.Comparer);

    [ThreadStatic]
    private static Stack<List<SubjectChildReference>>? _listPool;

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
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperties(subject, collectedSubjects, ProcessedPropertyStateMode.Seed);

                foreach (var child in collectedSubjects)
                {
                    AttachToProperty(child.Subject, subject.Context, child.Property, child.Index);
                }

                if (!_attachedSubjects.ContainsKey(subject))
                {
                    AttachToContext(subject, subject.Context);
                }
            }
        }
        finally
        {
            ReturnList(collectedSubjects);
        }
    }

    public void DetachSubjectFromContext(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperties(subject, collectedSubjects, ProcessedPropertyStateMode.Use);

                foreach (var child in collectedSubjects)
                {
                    DetachFromProperty(child.Subject, subject.Context, child.Property, child.Index);
                }

                DetachFromContext(subject, subject.Context);
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

        SubjectAttached?.Invoke(change);
        foreach (var propertyName in properties)
        {
            subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
        }
    }

    /// <summary>
    /// Attaches a subject via a property reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AttachToProperty(IInterceptorSubject subject, IInterceptorSubjectContext context,
        PropertyReference property, object? index, SubjectPropertyRelationship? relationship = null)
    {
        ref var set = ref CollectionsMarshal.GetValueRefOrAddDefault(_attachedSubjects, subject, out var existed);
        var isFirstAttach = !existed;
        if (!set.Add(property))
        {
            return;
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

        if (isFirstAttach)
        {
            SubjectAttached?.Invoke(change);

            foreach (var propertyName in properties)
            {
                subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
            }
        }
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
    private void DetachFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        if (!_attachedSubjects.Remove(subject))
        {
            return;
        }
        
        foreach (var entry in subject.Properties)
        {
            var property = new PropertyReference(subject, entry.Key);
            if (entry.Value is { IsIntercepted: true } && entry.Value.Type.CanContainSubjects())
                _processedProperties.Remove(property);

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
        InvokeRemovedLifecycleHandlers(subject, context, change);
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

        // Collect children and clean up in a single pass over properties
        List<SubjectChildReference>? children = null;
        if (isLastDetach)
        {
            _attachedSubjects.Remove(subject);

            foreach (var entry in subject.Properties)
            {
                var subjectProperty = new PropertyReference(subject, entry.Key);

                var metadata = entry.Value;
                if (metadata is { IsIntercepted: true } && metadata.Type.CanContainSubjects())
                {
                    // Use canonical processed state instead of the backing store, which may contain
                    // unattached children from a concurrent terminal write.
                    if (_processedProperties.TryGetValue(subjectProperty, out var processedState))
                    {
                        children ??= GetList();
                        AddMemberships(subjectProperty, processedState, children);
                    }

                    _processedProperties.Remove(subjectProperty);
                }

                subject.DetachSubjectProperty(subjectProperty);
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

        InvokeRemovedLifecycleHandlers(subject, context, change);

        if (children is not null)
        {
            foreach (var child in children)
            {
                DetachFromProperty(child.Subject, context, child.Property, child.Index);
            }

            ReturnList(children);
        }
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

        next(ref context);
        ReconcileStructuralProperty(context.Property, metadata, relationshipHandlers, materializeRelationships);
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
        ReconcileStructuralProperty(property, metadata, relationshipHandlers, materializeRelationships);
    }

    private void ReconcileStructuralProperty(
        PropertyReference property,
        SubjectPropertyMetadata metadata,
        ImmutableArray<IPropertyRelationshipHandler> relationshipHandlers,
        bool materializeRelationships)
    {
        lock (_attachedSubjects)
        {
            if (!_attachedSubjects.ContainsKey(property.Subject))
            {
                return;
            }

            if (!_reconcilingProperties.Add(property))
            {
                throw new InvalidOperationException(
                    $"Property '{property.Name}' on '{property.Subject.GetType().Name}' is already being reconciled.");
            }

            try
            {
                // The terminal write runs outside this lock. Re-read the actual backing value after acquiring
                // the writer lock so racing writes converge to the latest value visible to this authority.
                var value = metadata.GetValue?.Invoke(property.Subject);
                _processedProperties.TryGetValue(property, out var previousState);
                var reconciliation = SubjectPropertyRelationshipReconciler.Stage(
                    property,
                    value,
                    previousState,
                    materializeRelationships);

                // Enumeration can run user code which re-enters lifecycle operations because Monitor is
                // re-entrant. Do not commit a staged generation for a parent which left this authority.
                if (!_attachedSubjects.ContainsKey(property.Subject))
                {
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
                }

                foreach (var membership in reconciliation.MembershipAdditions)
                {
                    AttachToProperty(
                        membership.Subject,
                        property.Subject.Context,
                        property,
                        membership.FirstIndex,
                        membership.FirstRelationship);
                }

                _processedProperties[property] = reconciliation.State;

                if (reconciliation.State.Memberships.Length > reconciliation.MembershipAdditions.Length)
                {
                    var children = reconciliation.Children.AsSpan();
                    var lifecycleHandlers = property.Subject.Context.GetServices<IPropertyLifecycleHandler>();
                    for (var index = 0; index < lifecycleHandlers.Length; index++)
                    {
                        lifecycleHandlers[index].RefreshChildIndices(property, children);
                    }

                    if (property.Subject is IPropertyLifecycleHandler subjectHandler)
                    {
                        subjectHandler.RefreshChildIndices(property, children);
                    }
                }

                if (materializeRelationships)
                {
                    property.Subject.ReconcileChildRelationships(
                        relationshipHandlers,
                        property,
                        reconciliation.State.Relationships.AsSpan());
                }
            }
            finally
            {
                _reconcilingProperties.Remove(property);
            }
        }
    }

    private enum ProcessedPropertyStateMode
    {
        /// <summary>Read the backing store and seed canonical processed state during attach.</summary>
        Seed,

        /// <summary>Use canonical processed state during detach.</summary>
        Use
    }

    private void FindSubjectsInProperties(IInterceptorSubject subject,
        List<SubjectChildReference> collectedSubjects,
        ProcessedPropertyStateMode processedPropertyStateMode)
    {
        foreach (var property in subject.Properties)
        {
            var metadata = property.Value;
            if (!metadata.IsIntercepted ||
                !metadata.Type.CanContainSubjects())
            {
                continue;
            }

            var propertyReference = new PropertyReference(subject, property.Key);
            if (processedPropertyStateMode == ProcessedPropertyStateMode.Use)
            {
                if (_processedProperties.TryGetValue(propertyReference, out var processedState))
                {
                    AddMemberships(propertyReference, processedState, collectedSubjects);
                }

                continue;
            }

            var value = metadata.GetValue?.Invoke(subject);
            var reconciliation = SubjectPropertyRelationshipReconciler.Stage(
                propertyReference,
                value,
                previousState: null,
                materializeRelationships: false);
            _processedProperties[propertyReference] = reconciliation.State;
            AddMemberships(propertyReference, reconciliation.State, collectedSubjects);
        }
    }

    private static void AddMemberships(
        PropertyReference property,
        ProcessedPropertyState state,
        List<SubjectChildReference> collectedSubjects)
    {
        foreach (var membership in state.Memberships)
        {
            collectedSubjects.Add(new SubjectChildReference(
                membership.Subject,
                property,
                membership.FirstIndex));
        }
    }

    #region  Performance

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<SubjectChildReference> GetList()
    {
        _listPool ??= new Stack<List<SubjectChildReference>>();
        return _listPool.Count > 0 ? _listPool.Pop() : new List<SubjectChildReference>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnList(List<SubjectChildReference> list)
    {
        list.Clear();
        _listPool ??= new Stack<List<SubjectChildReference>>();
        _listPool.Push(list);
    }

    #endregion
}
