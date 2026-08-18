using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

    [ThreadStatic]
    private static int s_batchScopeCount;

    [ThreadStatic]
    private static IInterceptorSubjectContext? s_batchScopeRootContext;

    [ThreadStatic]
    private static LifecycleInterceptor? s_batchScopeOwner;

    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, (PropertyReference Property, object? Index)>? s_deferredLastDetaches;

    private sealed class BatchScope(LifecycleInterceptor lifecycle) : IDisposable
    {
        private readonly int _threadId = Environment.CurrentManagedThreadId;

        private bool _disposed;

        public void Dispose()
        {
            if (_threadId != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException(
                    "A lifecycle batch scope must be disposed on the thread that created it because its state is thread local. " +
                    "Do not hold a batch scope across an await.");
            }

            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lifecycle.EndBatchScope();
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
    /// Creates a batch scope that defers isLastDetach processing on the calling thread.
    /// Subjects whose last property reference is removed during the scope
    /// stay in _attachedSubjects as a present-but-empty entry. On dispose,
    /// only subjects whose entry is still empty are detached.
    /// PropertyReferenceRemoved/Added always fire immediately.
    /// </summary>
    /// <remarks>
    /// The scope state is thread local: the returned scope must be disposed on the thread that created it
    /// and must not be held across an await. Disposing it on another thread throws an
    /// <see cref="InvalidOperationException"/>. Only the outermost scope processes the deferred detaches,
    /// whichever nested scope happens to close last.
    /// Only the interceptor that opened the outermost scope defers: a scope opened by a different interceptor
    /// while one is already open defers nothing, so that interceptor's graph keeps reporting a last detach
    /// immediately and the transient detach of a subject moved between properties stays observable there.
    /// Under a scope a last-detach edge is reported twice: once immediately with
    /// <see cref="SubjectLifecycleChange.IsPropertyReferenceRemoved"/> alone, and once at scope close with
    /// <see cref="SubjectLifecycleChange.IsPropertyReferenceRemoved"/> and
    /// <see cref="SubjectLifecycleChange.IsContextDetach"/> together. Handlers must be idempotent.
    /// </remarks>
    /// <param name="rootContext">The context used to resolve the lifecycle handlers of the deferred detaches.</param>
    /// <returns>The scope which processes the deferred detaches when it is disposed.</returns>
    public IDisposable CreateBatchScope(IInterceptorSubjectContext rootContext)
    {
        ArgumentNullException.ThrowIfNull(rootContext);

        s_batchScopeCount++;
        if (s_batchScopeCount == 1)
        {
            s_batchScopeRootContext = rootContext;
            s_batchScopeOwner = this;
        }
        return new BatchScope(this);
    }

    private void EndBatchScope()
    {
        // No lock here: the scope state is thread static, so the decrement and the handover
        // of the deferred map need no synchronization.
        if (--s_batchScopeCount > 0)
        {
            return;
        }

        // Reset before invoking handlers: a handler that throws, or that opens a nested
        // scope, must not see or strand this scope's state.
        var deferred = s_deferredLastDetaches;
        var resolveContext = s_batchScopeRootContext;
        var owner = s_batchScopeOwner;

        s_deferredLastDetaches = null;
        s_batchScopeRootContext = null;
        s_batchScopeOwner = null;

        if (deferred is null || deferred.Count == 0 || resolveContext is null || owner is null)
        {
            return;
        }

        // Only the owner deferred anything, and the deferred entries live in its _attachedSubjects,
        // so the owner processes them even when a different instance closes the outermost scope.
        owner.ProcessDeferredDetaches(deferred, resolveContext);
    }

    private void ProcessDeferredDetaches(
        Dictionary<IInterceptorSubject, (PropertyReference Property, object? Index)> deferred,
        IInterceptorSubjectContext resolveContext)
    {
        List<Exception>? failures = null;

        lock (_attachedSubjects)
        {
            foreach (var (subject, deferredDetach) in deferred)
            {
                try
                {
                    ProcessDeferredDetach(subject, deferredDetach, resolveContext);
                }
                catch (Exception exception)
                {
                    // A throwing handler must not abandon the remaining entries: they would stay in
                    // _attachedSubjects as present-but-empty entries which can never be detached
                    // (the property is already removed) nor re-registered (the entry still exists).
                    (failures ??= []).Add(exception);
                }
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    private void ProcessDeferredDetach(
        IInterceptorSubject subject,
        (PropertyReference Property, object? Index) deferredDetach,
        IInterceptorSubjectContext resolveContext)
    {
        if (!_attachedSubjects.TryGetValue(subject, out var set) || !set.IsEmpty)
        {
            // Re-attached during the batch (entry not empty), skip.
            return;
        }

        // Genuinely orphaned, execute full detach.
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
                    FindSubjectsInProperty(subjectProperty, lastProcessed, children, null);
                }

                _lastProcessedValues.Remove(subjectProperty);
            }

            subject.DetachSubjectProperty(subjectProperty);
        }

        var count = subject.GetReferenceCount();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = deferredDetach.Property,
            Index = deferredDetach.Index,
            ReferenceCount = count,
            IsPropertyReferenceRemoved = true,
            IsContextDetach = true
        };

        try
        {
            SubjectDetaching?.Invoke(change);

            if (subject is ILifecycleHandler subjectHandler)
            {
                subjectHandler.HandleLifecycleChange(change);
            }

            // Use the root context for service resolution. The subject's own
            // context and intermediate parent contexts may have their fallbacks
            // removed by ContextInheritanceHandler during processing. The root
            // context never loses its fallback and can always resolve services.
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
            }
        }
        finally
        {
            if (children is not null)
            {
                ReturnList(children);
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

        // Only the interceptor that opened the outermost scope may defer: the deferred map is
        // thread-wide while _attachedSubjects is per-instance, so another instance's deferral
        // would be resolved against the wrong map at scope close and lost.
        var deferring = s_batchScopeCount > 0 && ReferenceEquals(s_batchScopeOwner, this);

        // Collect children and clean up in a single pass over properties
        List<(IInterceptorSubject subject, PropertyReference property, object? index)>? children = null;
        if (isLastDetach)
        {
            if (deferring)
            {
                // Defer the full detach. The entry stays in _attachedSubjects as a present-but-empty
                // PropertyReferenceSet (the ref-mutated struct is already empty), so a re-attach within
                // the batch is seen as existing (isFirstAttach == false). EndBatchScope runs the real
                // detach only for entries that are still empty.
                s_deferredLastDetaches ??= [];
                s_deferredLastDetaches[subject] = (property, index);
            }
            else
            {
                // Immediate detach (existing behavior). Structurally modifies _attachedSubjects,
                // so the ref to set must not be used after this point.
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
        }

        var count = subject.DecrementReferenceCount();
        var contextDetach = isLastDetach && !deferring;
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = count,
            IsPropertyReferenceRemoved = true,
            IsContextDetach = contextDetach
        };

        if (contextDetach)
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
