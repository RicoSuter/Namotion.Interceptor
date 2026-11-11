using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private readonly Dictionary<IInterceptorSubject, PropertyReferenceSet> _attachedSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, object?> _lastProcessedValues = new(PropertyReference.Comparer);

    // Roots whose children are being attached, which a write reconciles as attached subjects.
    private readonly HashSet<IInterceptorSubject> _attachingRoots = new(ReferenceEqualityComparer.Instance);

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

    /// <summary>
    /// Runs <paramref name="whenAttached"/> while the subject is provably in this interceptor's object
    /// graph, holding the lock that serializes graph mutation for the duration, and reports whether it
    /// ran.
    /// </summary>
    /// <remarks>
    /// A callback rather than a plain predicate, because the answer is only usable while it still
    /// holds: a caller that reads membership and then acts on it has released the lock in between, and
    /// a graph mutation that lands in that gap makes the action land on the opposite answer. Anything
    /// derived from membership therefore has to be written here.
    /// <para>
    /// The callback runs under the lock that serializes graph mutation, so it must not block, must not
    /// mutate the graph, and must not wait on anything that does. It is re-entrant on the calling
    /// thread, so an <see cref="ILifecycleHandler"/> may reach this while being invoked.
    /// </para>
    /// </remarks>
    /// <param name="subject">The subject.</param>
    /// <param name="whenAttached">Runs only if the subject is attached.</param>
    /// <returns>True when the subject was attached and the callback ran.</returns>
    public bool TryRunWhileAttached(IInterceptorSubject subject, Action whenAttached)
    {
        lock (_attachedSubjects)
        {
            if (!_attachedSubjects.ContainsKey(subject))
            {
                return false;
            }

            whenAttached();
            return true;
        }
    }

    /// <summary>
    /// The property references that hold the subject now. Read only while this interceptor dispatches a lifecycle
    /// change, which holds its lock.
    /// </summary>
    internal PropertyReferenceSet GetPropertyReferences(IInterceptorSubject subject)
    {
        return _attachedSubjects.TryGetValue(subject, out var set) ? set : default;
    }

    public void AttachSubjectToContext(IInterceptorSubject subject)
    {
        var children = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                // A subject attached through a property is already tracked; a root is tracked once its children are, and
                // is in _attachingRoots meanwhile.
                var isRoot = !_attachedSubjects.ContainsKey(subject) && _attachingRoots.Add(subject);
                try
                {
                    AttachProperties(subject, children, isRoot);
                }
                finally
                {
                    if (isRoot)
                    {
                        _attachingRoots.Remove(subject);
                    }
                }

                if (isRoot)
                {
                    AttachToContext(subject, subject.Context);
                }
            }
        }
        finally
        {
            ReturnList(children);
        }
    }

    // Each property is read and seeded when its turn comes. A handler that writes a property during the attach
    // reconciles it, so a property with a baseline is skipped: its children are attached, and a write still pending
    // on another thread reconciles from that baseline.
    private void AttachProperties(IInterceptorSubject subject,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> children, bool isRoot)
    {
        foreach (var property in subject.Properties)
        {
            var metadata = property.Value;
            if (!metadata.IsIntercepted || !metadata.Type.CanContainSubjects())
            {
                continue;
            }

            var propertyReference = new PropertyReference(subject, property.Key);
            if (_lastProcessedValues.ContainsKey(propertyReference))
            {
                continue;
            }

            var value = metadata.GetValue?.Invoke(subject);
            _lastProcessedValues[propertyReference] = value;

            FindSubjectsInProperty(propertyReference, value, children, null);
            AttachChildren(subject.Context, children, null, children.Count > 1 && HasRepeatedSubject(children));

            // A handler detached the subject during this attach. Its detach removed the baselines seeded so far
            // and the children attached before it; detach the ones attached after it and seed nothing more.
            if (!isRoot && !_attachedSubjects.ContainsKey(subject))
            {
                DetachChildren(subject.Context, children);
                break;
            }

            children.Clear();
        }
    }

    // Detaches in reverse order, which RemoveChild finds fastest. A child the property does not hold raises nothing.
    private void DetachChildren(IInterceptorSubjectContext context,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> children)
    {
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var (subject, property, index) = children[i];
            DetachFromProperty(subject, context, property, index);
        }
    }

    // Attaches the children of one property, except the subjects in kept, which the property held before. A child
    // linked before, or at an earlier place, raises nothing.
    private void AttachChildren(IInterceptorSubjectContext context,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> children,
        HashSet<IInterceptorSubject>? kept, bool hasRepeatedSubject)
    {
        if (hasRepeatedSubject)
        {
            AttachRepeatedChildren(context, children, kept);
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            var (subject, property, index) = children[i];
            if (kept is null || !kept.Contains(subject))
            {
                AttachToProperty(subject, context, property, index, default);
            }
        }
    }

    private static bool HasRepeatedSubject(
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> children)
    {
        var subjects = GetSubjectHashSet();
        try
        {
            foreach (var child in children)
            {
                if (!subjects.Add(child.subject))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            ReturnSubjectHashSet(subjects);
        }
    }

    // Attaches each subject at its first key or position with its later ones.
    private void AttachRepeatedChildren(IInterceptorSubjectContext context,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> children,
        HashSet<IInterceptorSubject>? kept)
    {
        // Rented per call: the handlers of an attach can start a nested write on this thread.
        var additionalIndices = AdditionalIndexCollector.Rent(CollectionsMarshal.AsSpan(children));
        try
        {
            for (var i = 0; i < children.Count; i++)
            {
                var (subject, property, index) = children[i];
                if (kept is null || !kept.Contains(subject))
                {
                    AttachToProperty(subject, context, property, index, additionalIndices[i]);
                }
            }
        }
        finally
        {
            AdditionalIndexCollector.Return(additionalIndices);
        }
    }

    public void DetachSubjectFromContext(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                FindAttachedSubjectsInProperties(subject, collectedSubjects);

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
    /// Attaches a subject via a property reference, unless the property already holds it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AttachToProperty(IInterceptorSubject subject, IInterceptorSubjectContext context,
        PropertyReference property, object? index, ImmutableArray<object?> additionalIndices)
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
            AdditionalIndices = additionalIndices,
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
            IsContextDetach = isLastDetach,
            Interceptor = this
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

            AttachChildren(context.Property.Subject.Context, newCollectedSubjects, oldTouchedSubjects,
                newTouchedSubjects.Count != newCollectedSubjects.Count);

            _lastProcessedValues[context.Property] = newValue;

            // Parent was concurrently detached between next() and lock acquisition.
            // Undo: remove dangling _lastProcessedValues and detach orphaned children.
            if (!_attachedSubjects.ContainsKey(context.Property.Subject) && !_attachingRoots.Contains(context.Property.Subject))
            {
                _lastProcessedValues.Remove(context.Property);
                DetachOrphanedSubjects(ref context, newCollectedSubjects, oldTouchedSubjects);

                return;
            }

            // Report kept subjects whose keys or positions changed. A write between two single references
            // keeps no subject, since writing the same one again returns early.
            if (lastProcessed is IEnumerable || newValue is IEnumerable)
            {
                ChildIndexRefresher.RefreshMovedChildren(context.Property,
                    oldCollectedSubjects, newCollectedSubjects, oldTouchedSubjects, newTouchedSubjects);
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

    // Reads each property from its baseline, which holds the subjects the property has attached.
    private void FindAttachedSubjectsInProperties(IInterceptorSubject subject,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects)
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
            var propertyValue = _lastProcessedValues.TryGetValue(propertyReference, out var lastProcessed)
                ? lastProcessed
                : metadata.GetValue?.Invoke(subject);

            if (propertyValue is not null)
            {
                FindSubjectsInProperty(propertyReference, propertyValue, collectedSubjects, null);
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
        // IDictionaryEnumerator hands out key and value separately, where foreach
        // would box a DictionaryEntry per entry.
        var enumerator = dictionary.GetEnumerator();
        try
        {
            while (enumerator.MoveNext())
            {
                if (enumerator.Value is IInterceptorSubject subjectItem)
                {
                    touchedSubjects?.Add(subjectItem);
                    collectedSubjects.Add((subjectItem, property, enumerator.Key));
                }
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
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
