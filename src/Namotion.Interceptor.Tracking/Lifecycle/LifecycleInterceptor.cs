using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private readonly Dictionary<IInterceptorSubject, PropertyReferenceSet> _attachedSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, object?> _lastProcessedValues = new(PropertyReference.Comparer);

    [ThreadStatic]
    private static Stack<List<(IInterceptorSubject subject, PropertyReference property, object? index)>>? _listPool;

    [ThreadStatic]
    private static Stack<HashSet<IInterceptorSubject>>? _subjectHashSetPool;

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
        PropertyReference property, object? index)
    {
        ref var set = ref CollectionsMarshal.GetValueRefOrAddDefault(_attachedSubjects, subject, out var existed);
        var isFirstAttach = !existed;
        if (!set.Add(property))
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
        ref var set = ref CollectionsMarshal.GetValueRefOrNullRef(_attachedSubjects, subject);
        if (Unsafe.IsNullRef(ref set) || !set.Remove(property))
        {
            return;
        }

        var isLastDetach = set.IsEmpty;

        // Collect children and clean up in a single pass over properties
        List<(IInterceptorSubject subject, PropertyReference property, object? index)>? children = null;
        if (isLastDetach)
        {
            _attachedSubjects.Remove(subject);
            children = DetachSubjectProperties(subject);
        }

        var count = subject.DecrementReferenceCount();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = count,
            IsPropertyReferenceRemoved = true,
            IsContextDetach = isLastDetach
        };

        if (isLastDetach)
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

    private List<(IInterceptorSubject subject, PropertyReference property, object? index)>? DetachSubjectProperties(
        IInterceptorSubject subject)
    {
        List<(IInterceptorSubject subject, PropertyReference property, object? index)>? children = null;
        foreach (var entry in subject.Properties)
        {
            var subjectProperty = new PropertyReference(subject, entry.Key);

            var metadata = entry.Value;
            if (metadata is { IsIntercepted: true } && metadata.Type.CanContainSubjects())
            {
                // Use _lastProcessedValues (what was actually attached) instead of the backing
                // store, which may contain unattached children from a concurrent next() call.
                if (_lastProcessedValues.TryGetValue(subjectProperty, out var lastProcessed) && lastProcessed is not null)
                {
                    children ??= GetList();
                    FindSubjectsInProperty(subjectProperty, lastProcessed, children, null);
                }

                _lastProcessedValues.Remove(subjectProperty);
            }

            subject.DetachSubjectProperty(subjectProperty);
        }

        return children;
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
    /// that is currently being reconciled, because this would corrupt the reconciliation baseline.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        var metadata = context.Property.Metadata;

        // A non-intercepted property stores nothing, so it owns no subject, yet derived notifications still
        // publish through this chain. The attach scan and both detach paths already skip these properties.
        if (!metadata.IsIntercepted || !metadata.Type.CanContainSubjects<TProperty>())
        {
            return;
        }

        lock (_attachedSubjects)
        {
            var lastProcessed = _lastProcessedValues.GetValueOrDefault(context.Property);

            // Read the actual backing store value to handle concurrent writes correctly.
            // context.NewValue may differ from the backing store if another thread
            // overwrote the property between our next() call and lock acquisition.
            var getValue = metadata.GetValue;
            var newValue = getValue is not null
                ? getValue(context.Property.Subject)
                : context.NewValue;

            if (ReferenceEquals(lastProcessed, newValue))
            {
                return;
            }

            if (IsScalarValue(lastProcessed) && IsScalarValue(newValue))
            {
                return;
            }

            ReconcilePropertySubjects(ref context, lastProcessed, newValue);
        }
    }

    private static bool IsScalarValue(object? value)
    {
        // Null transitions still need reconciliation to update the stored baseline.
        return value is not (null or IInterceptorSubject or IEnumerable) || value is string;
    }

    private void ReconcilePropertySubjects<TProperty>(ref PropertyWriteContext<TProperty> context,
        object? lastProcessed, object? newValue)
    {
        var oldCollectedSubjects = GetList();
        var newCollectedSubjects = GetList();
        var oldTouchedSubjects = GetSubjectHashSet();
        var newTouchedSubjects = GetSubjectHashSet();

        try
        {
            FindSubjectsInProperty(context.Property, lastProcessed, oldCollectedSubjects, oldTouchedSubjects);
            FindSubjectsInProperty(context.Property, newValue, newCollectedSubjects, newTouchedSubjects);

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

            // Parent was concurrently detached between next() and lock acquisition.
            // Undo: remove dangling _lastProcessedValues and detach orphaned children.
            if (!_attachedSubjects.ContainsKey(context.Property.Subject))
            {
                _lastProcessedValues.Remove(context.Property);
                DetachOrphanedSubjects(ref context, newCollectedSubjects, oldTouchedSubjects);

                return;
            }

            // Refresh child index metadata for retained subjects whose
            // positions may have shifted in the new collection.
            if (newValue is IEnumerable && oldTouchedSubjects.Overlaps(newTouchedSubjects))
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

    private void DetachOrphanedSubjects<TProperty>(ref PropertyWriteContext<TProperty> context,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> newCollectedSubjects,
        HashSet<IInterceptorSubject> oldTouchedSubjects)
    {
        for (var i = 0; i < newCollectedSubjects.Count; i++)
        {
            var (subject, property, index) = newCollectedSubjects[i];
            if (!oldTouchedSubjects.Contains(subject))
            {
                DetachFromProperty(subject, context.Property.Subject.Context, property, index);
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
                FindSubjectsInProperty(propertyReference, propertyValue, collectedSubjects, touchedSubjects);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FindSubjectsInProperty(PropertyReference property,
        object? value,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        // Hot paths (IDictionary, ICollection) come before string/IEnumerable so common
        // writes don't pay extra type checks. The IEnumerable case at the end handles read-only
        // types that implement neither ICollection nor IDictionary (e.g. custom IReadOnlyList /
        // IReadOnlyDictionary wrappers that opt out of the non-generic container interfaces).
        switch (value)
        {
            case null:
                return;

            case IInterceptorSubject subject:
                touchedSubjects?.Add(subject);
                collectedSubjects.Add((subject, property, null));
                return;

            case IDictionary dictionary:
                FindSubjectsInDictionary(property, dictionary, collectedSubjects, touchedSubjects);
                return;

            case ICollection collection:
            {
                var i = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject subjectItem)
                    {
                        touchedSubjects?.Add(subjectItem);
                        collectedSubjects.Add((subjectItem, property, i));
                    }
                    i++;
                }
                return;
            }

            case string:
                return;

            case IEnumerable enumerable:
                FindSubjectsInEnumerable(property, enumerable, collectedSubjects, touchedSubjects);
                return;
        }
    }

    private static void FindSubjectsInDictionary(PropertyReference property, IDictionary dictionary,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is IInterceptorSubject subjectItem)
            {
                touchedSubjects?.Add(subjectItem);
                collectedSubjects.Add((subjectItem, property, entry.Key));
            }
        }
    }

    private static void FindSubjectsInEnumerable(PropertyReference property, IEnumerable enumerable,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        // A pair carries its key, but a dictionary type is free to enumerate as its values
        // instead, and a broad declaration (object, plain interface) never classifies as a
        // dictionary, so the value's own type has to answer when the declared one cannot.
        var isKeyed = property.Metadata.Type.IsSubjectDictionaryType() ||
                      enumerable.GetType().IsSubjectDictionaryType();
        var index = 0;
        foreach (var item in enumerable)
        {
            if (isKeyed && item is not null &&
                SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var keyedItem))
            {
                touchedSubjects?.Add(keyedItem);
                collectedSubjects.Add((keyedItem, property, key));
            }
            else if (item is IInterceptorSubject subjectItem)
            {
                touchedSubjects?.Add(subjectItem);
                collectedSubjects.Add((subjectItem, property, index));
            }
            index++;
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
        return _subjectHashSetPool.Count > 0 ? _subjectHashSetPool.Pop() : new HashSet<IInterceptorSubject>(8, ReferenceEqualityComparer.Instance);
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
