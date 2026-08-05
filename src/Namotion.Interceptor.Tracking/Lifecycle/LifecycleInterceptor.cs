using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private readonly Dictionary<IInterceptorSubject, PropertyReferenceSet> _attachedSubjects = [];
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
                    AttachRootSubject(subject, subject.Context);
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

                DetachRootSubject(subject, subject.Context);
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
    private void AttachRootSubject(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        ThrowIfDetachIsUnwinding(subject);

        // Both attach entry points claim ownership. This one is the only code that runs for a
        // childless root, and without it such a root carries an attach record with no owner, so a
        // second graph's property attach finds _owner null and claims it: the subject then holds an
        // attach edge into one graph and a parent link into another and resolves both. That is the
        // half-attached state behaviour change 5 exists to make unreachable.
        subject.GetExecutor().ClaimOwnership(this, context);

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
        ThrowIfDetachIsUnwinding(subject);

        // Before any mutation, so a cross-graph rejection leaves this subject's bookkeeping clean.
        // What it cannot undo is the property write itself, which WriteProperty already committed
        // through next(), nor earlier items of the same batch. That partial batch is #384's shape.
        subject.GetExecutor().ClaimOwnership(this, context);

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
    
    /// <summary>
    /// Within one graph, absent from the ledger while holding a parent link is exactly and only a
    /// detach still unwinding: DetachFromProperty removes the entry before the handlers run and
    /// clears the link afterwards. Re-attaching there would set a link on a subject that is
    /// currently a link source with no attach edge, which the cycle argument assumes impossible,
    /// and the resulting parent-only cycle is unrecoverable because the detach path itself would
    /// then throw and the link is internal.
    ///
    /// The ledger is per-interceptor, so the condition also matches a live child that another
    /// interceptor attached, whether a co-resolved one in the same graph or one in a different
    /// graph. Restricting the check to the owner separates them: a co-resolved interceptor is not
    /// the owner and passes, and a different graph is rejected one line later by ClaimOwnership,
    /// which is the accurate diagnosis there. The message still names both causes because the owner
    /// of a subject mid-detach cannot tell which of the two the caller meant.
    /// </summary>
    private void ThrowIfDetachIsUnwinding(IInterceptorSubject subject)
    {
        var executor = subject.GetExecutor();
        if (!_attachedSubjects.ContainsKey(subject) && executor.IsOwnedBy(this) && executor.HasParentContext)
        {
            throw new InvalidOperationException(
                $"Subject '{subject.GetType().FullName}' cannot be attached here: it is either being detached right now, " +
                "in which case it cannot be re-attached from inside a lifecycle callback, or it is a live child of another " +
                "lifecycle graph, in which case it must leave that graph first.");
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
    private void DetachRootSubject(IInterceptorSubject subject, IInterceptorSubjectContext context)
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

        try
        {
            SubjectDetaching?.Invoke(change);
            InvokeRemovedLifecycleHandlers(subject, context, change);
        }
        finally
        {
            // Paired with the claim in AttachRootSubject. Without it a detached root stays owned
            // forever: IsAttached() keeps reporting true and any later attach to a different graph is
            // rejected by an owner that no longer means anything. It sits after the handlers for the
            // same reason the property path's release does, and in a finally for the same reason
            // DetachFromContext removes the edge in one: the record and the edge are already gone by
            // the time a handler throws, so an owner left standing here belongs to no graph and can
            // never be cleared through the public API. The descent cannot reach it for a subject
            // it already removed from the ledger, because the guard at the top of this method returns
            // first, and reaching it after the property path released finds no owner and no-ops. A
            // consumer calling DetachSubjectFromContext directly can still reach it for a referenced
            // subject, which is one more way that documented low-level call bypasses the guards.
            //
            // The ledger removal above is what entitles this call to release: it can only succeed
            // for a subject this interceptor claimed, which is the precondition ReleaseOwnership's
            // recorded set relies on now that it no longer resolves.
            subject.GetExecutor().ReleaseOwnership(this);
        }
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
        }

        var count = subject.DecrementReferenceCount(out var attachContextAtDecrement);
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = count,
            IsPropertyReferenceRemoved = true,
            IsContextDetach = isLastDetach
        };

        try
        {
            if (isLastDetach)
            {
                SubjectDetaching?.Invoke(change);
            }

            InvokeRemovedLifecycleHandlers(subject, context, change);
        }
        finally
        {
            // After the handlers, never before. The descent resolves the next level's handlers
            // through the child's own context, and a property-attached subject has no other edge,
            // so releasing first would make grandchildren get bookkeeping but no handler
            // invocation, and would lose this subject's own per-property deregistration too.
            // It also closes the window in which the subject is unowned while its graph is still
            // mid-detach, during which another graph could claim it.
            if (count == 0)
            {
                var executor = subject.GetExecutor();
                executor.TryClearParentContext();

                // The record captured with the decrement, not the live one: an AttachToContext that
                // lands after the count was decided has already published its own record and edge,
                // and releasing those would dismantle an attach that is still running.
                executor.ReleaseAttachEdge(attachContextAtDecrement);

                // Entitled to release because the set removal at the top of this method succeeded,
                // which only a subject this interceptor claimed can have an entry for.
                executor.ReleaseOwnership(this);
            }
        }

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
    /// that is currently being reconciled, because this would corrupt the reconciliation baseline.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        var metadata = context.Property.Metadata;
        if (!metadata.Type.CanContainSubjects<TProperty>())
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

            if ((lastProcessed is not (null or IInterceptorSubject or IEnumerable) || lastProcessed is string) &&
                (newValue is not (null or IInterceptorSubject or IEnumerable) || newValue is string))
            {
                return;
            }

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
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subjectItem)
                    {
                        touchedSubjects?.Add(subjectItem);
                        collectedSubjects.Add((subjectItem, property, entry.Key));
                    }
                }
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
                // Read-only types (no ICollection): dispatch on declared property shape.
                if (property.Metadata.Type.IsSubjectDictionaryType())
                {
                    foreach (var item in enumerable)
                    {
                        if (item is null) continue;
                        if (SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subjectItem))
                        {
                            touchedSubjects?.Add(subjectItem);
                            collectedSubjects.Add((subjectItem, property, key));
                        }
                    }
                }
                else
                {
                    var i = 0;
                    foreach (var item in enumerable)
                    {
                        if (item is IInterceptorSubject subjectItem)
                        {
                            touchedSubjects?.Add(subjectItem);
                            collectedSubjects.Add((subjectItem, property, i));
                        }
                        i++;
                    }
                }
                return;
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
