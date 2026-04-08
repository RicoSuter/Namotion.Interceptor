using System.Collections;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private readonly Dictionary<IInterceptorSubject, HashSet<PropertyReference>> _attachedSubjects = [];
    private readonly Dictionary<PropertyReference, object?> _lastProcessedValues = new(PropertyReference.Comparer);

    [ThreadStatic]
    private static Stack<List<(IInterceptorSubject subject, PropertyReference property, object? index)>>? _listPool;

    [ThreadStatic]
    private static Stack<HashSet<IInterceptorSubject>>? _subjectHashSetPool;

    [ThreadStatic]
    private static int s_batchScopeCount;

    [ThreadStatic]
    private static IInterceptorSubjectContext? s_batchScopeRootContext;

    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, PropertyReference>? s_deferredLastDetaches;

    private sealed class BatchScope(LifecycleInterceptor lifecycle) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lifecycle.EndBatchScope();
            }
        }
    }

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

    /// <summary>
    /// Creates a batch scope that defers isLastDetach processing.
    /// Subjects whose last property reference is removed during the scope
    /// stay in _attachedSubjects with an empty set. On dispose, only
    /// subjects still with an empty set are detached.
    /// PropertyReferenceRemoved/Added always fire immediately.
    /// </summary>
    public IDisposable CreateBatchScope(IInterceptorSubjectContext rootContext)
    {
        s_batchScopeCount++;
        s_deferredLastDetaches ??= [];
        if (s_batchScopeCount == 1)
        {
            s_batchScopeRootContext = rootContext;
        }
        return new BatchScope(this);
    }

    private void EndBatchScope()
    {
        lock (_attachedSubjects)
        {
            s_batchScopeCount--;
            if (s_batchScopeCount == 0 && s_deferredLastDetaches is { Count: > 0 })
            {
                foreach (var (subject, deferredProperty) in s_deferredLastDetaches)
                {
                    if (_attachedSubjects.TryGetValue(subject, out var set) && set.Count == 0)
                    {
                        // Genuinely orphaned — execute full detach.
                        _attachedSubjects.Remove(subject);

                        List<(IInterceptorSubject subject, PropertyReference property, object? index)>? children = null;
                        foreach (var entry in subject.Properties)
                        {
                            var subjectProperty = new PropertyReference(subject, entry.Key);
                            var metadata = entry.Value;
                            if (metadata is { IsIntercepted: true } && metadata.Type.CanContainSubjects())
                            {
                                if (_lastProcessedValues.TryGetValue(subjectProperty, out var lastProcessed) && lastProcessed is not null)
                                {
                                    children ??= GetList();
                                    FindSubjectsInProperty(subjectProperty, lastProcessed, null, children, null);
                                }

                                _lastProcessedValues.Remove(subjectProperty);
                            }

                            subject.DetachSubjectProperty(subjectProperty);
                        }

                        var count = subject.GetReferenceCount();
                        var change = new SubjectLifecycleChange
                        {
                            Subject = subject,
                            Property = deferredProperty,
                            ReferenceCount = count,
                            IsPropertyReferenceRemoved = true,
                            IsContextDetach = true
                        };

                        SubjectDetaching?.Invoke(change);

                        if (subject is ILifecycleHandler subjectHandler)
                        {
                            subjectHandler.HandleLifecycleChange(change);
                        }

                        // Use the root context for service resolution. The subject's own
                        // context and intermediate parent contexts may have their fallbacks
                        // removed by ContextInheritanceHandler during processing. The root
                        // context never loses its fallback and can always resolve services.
                        var resolveContext = s_batchScopeRootContext!;
                        var array = resolveContext.GetServices<ILifecycleHandler>();
                        for (var i = 0; i < array.Length; i++)
                        {
                            array[i].HandleLifecycleChange(change);
                        }

                        if (children is not null)
                        {
                            foreach (var child in children)
                            {
                                DetachFromProperty(child.subject, resolveContext, child.property, child.index);
                            }

                            ReturnList(children);
                        }
                    }
                    // else: re-attached during batch → skip
                }

                s_deferredLastDetaches.Clear();
                s_batchScopeRootContext = null;
            }
        }
    }

    public void AttachSubjectToContext(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperties(subject, collectedSubjects, null, LastProcessedValuesMode.Seed);

                foreach (var child in collectedSubjects)
                {
                    AttachToProperty(child.subject, subject.Context, child.property, child.index);
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
                FindSubjectsInProperties(subject, collectedSubjects, null, LastProcessedValuesMode.Use);

                foreach (var child in collectedSubjects)
                {
                    DetachFromProperty(child.subject, subject.Context, child.property, child.index);
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
        var isFirstAttach = _attachedSubjects.TryAdd(subject, []);
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
        PropertyReference property, object? index)
    {
        var isFirstAttach = _attachedSubjects.TryAdd(subject, []);
        if (!_attachedSubjects[subject].Add(property))
        {
            return;
        }

        var count = subject.IncrementReferenceCount();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = count,
            IsContextAttach = isFirstAttach,
            IsPropertyReferenceAdded = true
        };

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
                _lastProcessedValues.Remove(property);

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
        PropertyReference property, object? index)
    {
        if (!_attachedSubjects.TryGetValue(subject, out var set) || !set.Remove(property))
        {
            return;
        }

        var isLastDetach = set.Count == 0;

        // Collect children and clean up in a single pass over properties
        List<(IInterceptorSubject subject, PropertyReference property, object? index)>? children = null;
        if (isLastDetach)
        {
            if (s_batchScopeCount > 0)
            {
                // Defer the full detach — subject stays in _attachedSubjects with empty set.
                // PropertyReferenceRemoved still fires below (per-link, always immediate).
                // ContextDetach deferred until scope dispose.
                s_deferredLastDetaches ??= [];
                s_deferredLastDetaches[subject] = property;
            }
            else
            {
                // Immediate detach (existing behavior)
                _attachedSubjects.Remove(subject);

                foreach (var entry in subject.Properties)
                {
                    var subjectProperty = new PropertyReference(subject, entry.Key);

                    var metadata = entry.Value;
                    if (metadata is { IsIntercepted: true } && metadata.Type.CanContainSubjects())
                    {
                        if (_lastProcessedValues.TryGetValue(subjectProperty, out var lastProcessed) && lastProcessed is not null)
                        {
                            children ??= GetList();
                            FindSubjectsInProperty(subjectProperty, lastProcessed, null, children, null);
                        }

                        _lastProcessedValues.Remove(subjectProperty);
                    }

                    subject.DetachSubjectProperty(subjectProperty);
                }
            }
        }

        var count = subject.DecrementReferenceCount();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = count,
            IsPropertyReferenceRemoved = true,
            IsContextDetach = isLastDetach && s_batchScopeCount == 0
        };

        if (isLastDetach && s_batchScopeCount == 0)
        {
            SubjectDetaching?.Invoke(change);
        }

        InvokeRemovedLifecycleHandlers(subject, context, change);

        if (children is not null)
        {
            foreach (var child in children)
            {
                DetachFromProperty(child.subject, context, child.property, child.index);
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
    /// Re-entrant for different properties (lock is re-entrant, each property has its own
    /// <c>_lastProcessedValues</c> entry). Handlers must NOT write to the same property
    /// that is currently being reconciled — this would corrupt the reconciliation baseline.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        if (!context.Property.Metadata.Type.CanContainSubjects<TProperty>())
        {
            return;
        }

        lock (_attachedSubjects)
        {
            if (!_lastProcessedValues.TryGetValue(context.Property, out var lastProcessed))
                lastProcessed = context.CurrentValue;

            // Read the actual backing store value to handle concurrent writes correctly.
            // context.NewValue may differ from the backing store if another thread
            // overwrote the property between our next() call and lock acquisition.
            var getValue = context.Property.Metadata.GetValue;
            var newValue = getValue is not null
                ? getValue(context.Property.Subject)
                : context.NewValue;

            if (ReferenceEquals(lastProcessed, newValue))
            {
                return;
            }

            if (lastProcessed is not (null or IInterceptorSubject or ICollection or IDictionary) &&
                newValue is not (null or IInterceptorSubject or ICollection or IDictionary))
            {
                return;
            }

            var oldCollectedSubjects = GetList();
            var newCollectedSubjects = GetList();
            var oldTouchedSubjects = GetSubjectHashSet();
            var newTouchedSubjects = GetSubjectHashSet();

            try
            {
                FindSubjectsInProperty(context.Property, lastProcessed, null, oldCollectedSubjects, oldTouchedSubjects);
                FindSubjectsInProperty(context.Property, newValue, null, newCollectedSubjects, newTouchedSubjects);

                // Detach in reverse order so that collection children are removed from the end first.
                // RemoveChild searches backwards to match this order for O(1) per removal.
                for (var i = oldCollectedSubjects.Count - 1; i >= 0; i--)
                {
                    var (subject, property, index) = oldCollectedSubjects[i];
                    if (!newTouchedSubjects.Contains(subject))
                    {
                        DetachFromProperty(subject, context.Property.Subject.Context, property, index);
                    }
                }

                for (var i = 0; i < newCollectedSubjects.Count; i++)
                {
                    var (subject, property, index) = newCollectedSubjects[i];
                    if (!oldTouchedSubjects.Contains(subject))
                    {
                        AttachToProperty(subject, context.Property.Subject.Context, property, index);
                    }
                }

                _lastProcessedValues[context.Property] = newValue;

                // Parent was concurrently detached between next() and lock acquisition —
                // undo: remove dangling _lastProcessedValues and detach orphaned children.
                if (!_attachedSubjects.ContainsKey(context.Property.Subject))
                {
                    _lastProcessedValues.Remove(context.Property);
                    for (var i = 0; i < newCollectedSubjects.Count; i++)
                    {
                        var (subject, property, index) = newCollectedSubjects[i];
                        if (!oldTouchedSubjects.Contains(subject))
                        {
                            DetachFromProperty(subject, context.Property.Subject.Context, property, index);
                        }
                    }

                    return;
                }

                // Refresh child index metadata for retained subjects whose
                // positions may have shifted in the new collection.
                if (newValue is ICollection && oldTouchedSubjects.Overlaps(newTouchedSubjects))
                {
                    var handlers = context.Property.Subject.Context.GetServices<IPropertyLifecycleHandler>();
                    for (var i = 0; i < handlers.Length; i++)
                    {
                        handlers[i].RefreshCollectionProperty(context.Property, newValue);
                    }
                }
            }
            finally
            {
                ReturnList(oldCollectedSubjects);
                ReturnList(newCollectedSubjects);
                ReturnSubjectHashSet(oldTouchedSubjects);
                ReturnSubjectHashSet(newTouchedSubjects);
            }
        }
    }

    private enum LastProcessedValuesMode
    {
        /// <summary>Read property values from the backing store (default).</summary>
        None,

        /// <summary>Read from backing store and seed _lastProcessedValues (used during attach).</summary>
        Seed,

        /// <summary>Read from _lastProcessedValues instead of backing store (used during detach).</summary>
        Use
    }

    private void FindSubjectsInProperties(IInterceptorSubject subject,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects,
        LastProcessedValuesMode lastProcessedValuesMode = LastProcessedValuesMode.None)
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
            var propertyValue = lastProcessedValuesMode == LastProcessedValuesMode.Use && _lastProcessedValues.TryGetValue(propertyReference, out var lastProcessed)
                ? lastProcessed
                : metadata.GetValue?.Invoke(subject);

            if (lastProcessedValuesMode == LastProcessedValuesMode.Seed)
            {
                _lastProcessedValues[propertyReference] = propertyValue;
            }

            if (propertyValue is not null)
            {
                FindSubjectsInProperty(propertyReference, propertyValue, null, collectedSubjects, touchedSubjects);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FindSubjectsInProperty(PropertyReference property,
        object? value, object? index,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        switch (value)
        {
            case IInterceptorSubject subject:
                touchedSubjects?.Add(subject);
                collectedSubjects.Add((subject, property, index));
                break;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subjectItem)
                    {
                        touchedSubjects?.Add(subjectItem);
                        collectedSubjects.Add((subjectItem, property, entry.Key));
                    }
                }
                break;

            // TODO: Support more enumerations with high performance here (immutable arrays, lists, ...)
            // case string collection: break;
            // case IEnumerable collection:

            case ICollection collection:
                var i = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject subject)
                    {
                        touchedSubjects?.Add(subject);
                        collectedSubjects.Add((subject, property, i));
                    }
                    i++;
                }
                break;
        }
    }

    #region  Performance

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<(IInterceptorSubject subject, PropertyReference property, object? index)> GetList()
    {
        _listPool ??= new Stack<List<(IInterceptorSubject, PropertyReference, object?)>>();
        return _listPool.Count > 0 ? _listPool.Pop() : new List<(IInterceptorSubject, PropertyReference, object?)>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HashSet<IInterceptorSubject> GetSubjectHashSet()
    {
        _subjectHashSetPool ??= new Stack<HashSet<IInterceptorSubject>>();
        return _subjectHashSetPool.Count > 0 ? _subjectHashSetPool.Pop() : new HashSet<IInterceptorSubject>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnList(List<(IInterceptorSubject, PropertyReference, object?)> list)
    {
        list.Clear();
        _listPool ??= new Stack<List<(IInterceptorSubject, PropertyReference, object?)>>();
        _listPool.Push(list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnSubjectHashSet(HashSet<IInterceptorSubject> hashSet)
    {
        hashSet.Clear();
        _subjectHashSetPool ??= new Stack<HashSet<IInterceptorSubject>>();
        _subjectHashSetPool.Push(hashSet);
    }

    #endregion
}
