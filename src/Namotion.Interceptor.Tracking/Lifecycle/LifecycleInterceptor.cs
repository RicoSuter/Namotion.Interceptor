using System.Collections;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private readonly Dictionary<IInterceptorSubject, HashSet<PropertyReference?>> _attachedSubjects = [];

    [ThreadStatic]
    private static Stack<List<(IInterceptorSubject subject, PropertyReference property, object? index)>>? _listPool;

    [ThreadStatic]
    private static Stack<HashSet<IInterceptorSubject>>? _hashSetPool;

    /// <summary>
    /// Raised when a subject is attached to the object graph.
    /// Handlers must be exception-free and fast (invoked inside lock).
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectAttached;

    /// <summary>
    /// Raised when a subject is detached from the object graph.
    /// Handlers must be exception-free and fast (invoked inside lock).
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectDetached;

    public void AttachSubjectToContext(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperties(subject, collectedSubjects, null);

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
                FindSubjectsInProperties(subject, collectedSubjects, null);

                DetachFromContext(subject, subject.Context);
               
                foreach (var child in collectedSubjects)
                {
                    DetachFromProperty(child.subject, subject.Context, child.property, child.index);
                }
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
        foreach (var handler in context.GetServices<ILifecycleHandler>())
        {
            handler.OnLifecycleEvent(change);
        }

        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.OnLifecycleEvent(change);
        }

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
        foreach (var handler in context.GetServices<ILifecycleHandler>())
        {
            handler.OnLifecycleEvent(change);
        }

        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.OnLifecycleEvent(change);
        }

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
    /// Detaches a subject from a context (root subject, no property reference).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DetachFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        if (!_attachedSubjects.Remove(subject, out _))
        {
            return;
        }

        foreach (var propertyName in subject.Properties.Keys)
        {
            subject.DetachSubjectProperty(new PropertyReference(subject, propertyName));
        }

        var count = subject.GetReferenceCount();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = count,
            IsContextDetach = true
        };

        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.OnLifecycleEvent(change);
        }

        foreach (var handler in context.GetServices<ILifecycleHandler>())
        {
            handler.OnLifecycleEvent(change);
        }

        SubjectDetached?.Invoke(change);
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

        // Collect children before detaching (for cascade)
        List<(IInterceptorSubject subject, PropertyReference property, object? index)>? children = null;
        if (isLastDetach)
        {
            children = GetList();
            FindSubjectsInProperties(subject, children, null);

            _attachedSubjects.Remove(subject);

            foreach (var propertyName in subject.Properties.Keys)
            {
                subject.DetachSubjectProperty(new PropertyReference(subject, propertyName));
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
            IsContextDetach = isLastDetach
        };

        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.OnLifecycleEvent(change);
        }

        foreach (var handler in context.GetServices<ILifecycleHandler>())
        {
            handler.OnLifecycleEvent(change);
        }

        if (isLastDetach)
        {
            SubjectDetached?.Invoke(change);

            // Cascade detach to children
            if (children is not null)
            {
                foreach (var child in children)
                {
                    DetachFromProperty(child.subject, context, child.property, child.index);
                }

                ReturnList(children);
            }
        }
    }

    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var currentValue = context.CurrentValue;
        next(ref context);
        var newValue = context.GetFinalValue();

        context.Property.SetWriteTimestamp(SubjectChangeContext.Current.ChangedTimestamp);

        if (typeof(TProperty).IsValueType || typeof(TProperty) == typeof(string))
        {
            return;
        }

        if (currentValue is not (IInterceptorSubject or ICollection or IDictionary) &&
            newValue is not (IInterceptorSubject or ICollection or IDictionary))
        {
            return;
        }

        var oldCollectedSubjects = GetList();
        var newCollectedSubjects = GetList();
        var oldTouchedSubjects = GetHashSet();
        var newTouchedSubjects = GetHashSet();

        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperty(context.Property, currentValue, null, oldCollectedSubjects, oldTouchedSubjects);
                FindSubjectsInProperty(context.Property, newValue, null, newCollectedSubjects, newTouchedSubjects);

                for (var i = oldCollectedSubjects.Count - 1; i >= 0; i--)
                {
                    var d = oldCollectedSubjects[i];
                    if (!newTouchedSubjects.Contains(d.subject))
                    {
                        DetachFromProperty(d.subject, context.Property.Subject.Context, d.property, d.index);
                    }
                }

                for (var i = 0; i < newCollectedSubjects.Count; i++)
                {
                    var d = newCollectedSubjects[i];
                    if (!oldTouchedSubjects.Contains(d.subject))
                    {
                        AttachToProperty(d.subject, context.Property.Subject.Context, d.property, d.index);
                    }
                }
            }
        }
        finally
        {
            ReturnList(oldCollectedSubjects);
            ReturnList(newCollectedSubjects);
            ReturnHashSet(oldTouchedSubjects);
            ReturnHashSet(newTouchedSubjects);
        }
    }

    private void FindSubjectsInProperties(IInterceptorSubject subject,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        foreach (var property in subject.Properties)
        {
            var metadata = property.Value;
            if (metadata.IsDerived || 
                metadata.IsIntercepted == false ||
                metadata.Type.IsValueType || 
                metadata.Type == typeof(string))
            {
                continue;
            }

            var propertyValue = metadata.GetValue?.Invoke(subject);
            if (propertyValue is not null)
            {
                FindSubjectsInProperty(new PropertyReference(subject, property.Key), propertyValue, null, collectedSubjects, touchedSubjects);
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
        return _listPool.Count > 0 ? _listPool.Pop() : [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HashSet<IInterceptorSubject> GetHashSet()
    {
        _hashSetPool ??= new Stack<HashSet<IInterceptorSubject>>();
        return _hashSetPool.Count > 0 ? _hashSetPool.Pop() : [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnList(List<(IInterceptorSubject, PropertyReference, object?)> list)
    {
        list.Clear();
        _listPool!.Push(list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnHashSet(HashSet<IInterceptorSubject> hashSet)
    {
        hashSet.Clear();
        _hashSetPool!.Push(hashSet);
    }

    #endregion
}