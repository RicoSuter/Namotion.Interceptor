using System.Collections;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private const string ReferenceCountKey = "Namotion.Interceptor.Tracking.ReferenceCount";

    private readonly Dictionary<IInterceptorSubject, HashSet<PropertyReference?>> _attachedSubjects = [];

    // Re-entrancy-safe, thread-local collection pooling
    [ThreadStatic]
    private static Stack<List<(IInterceptorSubject subject, PropertyReference property, object? index)>>? _listPool;

    [ThreadStatic]
    private static Stack<HashSet<IInterceptorSubject>>? _hashSetPool;

    public void AttachTo(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperties(subject, collectedSubjects, null);

                foreach (var child in collectedSubjects)
                {
                    AttachTo(child.subject, subject.Context, child.property, child.index);
                }

                if (!_attachedSubjects.ContainsKey(subject))
                {
                    AttachTo(subject, subject.Context, null, null);
                }
            }
        }
        finally
        {
            ReturnList(collectedSubjects);
        }
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperties(subject, collectedSubjects, null);

                DetachFrom(subject, subject.Context, null, null);
                foreach (var child in collectedSubjects)
                {
                    DetachFrom(child.subject, subject.Context, child.property, child.index);
                }
            }
        }
        finally
        {
            ReturnList(collectedSubjects);
        }
    }

    private void AttachTo(IInterceptorSubject subject, IInterceptorSubjectContext context, PropertyReference? property, object? index)
    {
        var firstAttach = _attachedSubjects.TryAdd(subject, []);
        if (_attachedSubjects[subject].Add(property))
        {
            var count = subject.Data.AddOrUpdate((null, ReferenceCountKey), 1, (_, count) => (int)count! + 1) as int?;
            var registryContext = new SubjectLifecycleChange(subject, property, index, count ?? 1);

            var properties = subject.Properties.Keys;

            foreach (var handler in context.GetServices<ILifecycleHandler>())
            {
                handler.AttachSubject(registryContext);
            }

            if (subject is ILifecycleHandler lifecycleHandler)
            {
                lifecycleHandler.AttachSubject(registryContext);
            }

            if (firstAttach)
            {
                foreach (var propertyName in properties)
                {
                    subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
                }
            }
        }
    }

    private void DetachFrom(IInterceptorSubject subject, IInterceptorSubjectContext context, PropertyReference? property, object? index)
    {
        if (_attachedSubjects.TryGetValue(subject, out var set) && set.Remove(property))
        {
            if (set.Count == 0)
            {
                _attachedSubjects.Remove(subject);

                foreach (var propertyName in subject.Properties.Keys)
                {
                    subject.DetachSubjectProperty(new PropertyReference(subject, propertyName));
                }
            }

            var count = subject.Data.AddOrUpdate((null, ReferenceCountKey), 0, (_, count) => (int)count! - 1) as int?;
            var registryContext = new SubjectLifecycleChange(subject, property, index, count ?? 1);
            if (subject is ILifecycleHandler lifecycleHandler)
            {
                lifecycleHandler.DetachSubject(registryContext);
            }

            foreach (var handler in context.GetServices<ILifecycleHandler>())
            {
                handler.DetachSubject(registryContext);
            }
        }
    }

    public void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next)
    {
        var currentValue = context.CurrentValue;
        next(ref context);
        var newValue = context.GetCurrentValue();

        context.Property.SetWriteTimestamp(SubjectMutationContext.GetCurrentTimestamp());

        if (typeof(TProperty).IsValueType || ReferenceEquals(currentValue, newValue))
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
                        DetachFrom(d.subject, context.Property.Subject.Context, d.property, d.index);
                    }
                }

                for (var i = 0; i < newCollectedSubjects.Count; i++)
                {
                    var d = newCollectedSubjects[i];
                    if (!oldTouchedSubjects.Contains(d.subject))
                    {
                        AttachTo(d.subject, context.Property.Subject.Context, d.property, d.index);
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
            if (property.Value.IsDerived)
                continue;

            var childValue = property.Value.GetValue?.Invoke(subject);
            if (childValue is not null)
            {
                var propertyRef = new PropertyReference(subject, property.Key);
                FindSubjectsInProperty(propertyRef, childValue, null, collectedSubjects, touchedSubjects);
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
            case IInterceptorSubject interceptorSubject:
                if (touchedSubjects?.Add(interceptorSubject) != false)
                {
                    collectedSubjects.Add((interceptorSubject, property, index));
                }
                break;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subjectItem && touchedSubjects?.Add(subjectItem) != false)
                    {
                        collectedSubjects.Add((subjectItem, property, entry.Key));
                    }
                }
                break;

            case ICollection collection:
                var i = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject subject && touchedSubjects?.Add(subject) != false)
                    {
                        collectedSubjects.Add((subject, property, i));
                    }
                    i++;
                }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<(IInterceptorSubject subject, PropertyReference property, object? index)> GetList()
    {
        _listPool ??= new Stack<List<(IInterceptorSubject, PropertyReference, object?)>>();
        return _listPool.Count > 0 ? _listPool.Pop() : new List<(IInterceptorSubject, PropertyReference, object?)>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnList(List<(IInterceptorSubject, PropertyReference, object?)> list)
    {
        list.Clear();
        _listPool!.Push(list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HashSet<IInterceptorSubject> GetHashSet()
    {
        _hashSetPool ??= new Stack<HashSet<IInterceptorSubject>>();
        return _hashSetPool.Count > 0 ? _hashSetPool.Pop() : new HashSet<IInterceptorSubject>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnHashSet(HashSet<IInterceptorSubject> set)
    {
        set.Clear();
        _hashSetPool!.Push(set);
    }
}