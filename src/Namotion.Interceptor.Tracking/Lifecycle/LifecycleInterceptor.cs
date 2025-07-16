using System.Collections;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor
{
    // TODO(perf): Profile and improve this class, high potential to improve

    private const string ReferenceCountKey = "Namotion.Interceptor.Tracking.ReferenceCount";
    
    private readonly Dictionary<IInterceptorSubject, HashSet<PropertyReference?>> _attachedSubjects = [];

    public void AttachTo(IInterceptorSubject subject)
    {
        lock (_attachedSubjects)
        {
            var touchedSubjects = new HashSet<IInterceptorSubject>();
            var collectedSubjects = new HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)>();
            FindSubjectsInProperties(subject, collectedSubjects, touchedSubjects);

            foreach (var child in collectedSubjects)
            {
                AttachTo(child.subject, subject.Context, child.property, child.index);
            }

            var alreadyAttached = _attachedSubjects.ContainsKey(subject);
            if (!alreadyAttached)
            {
                AttachTo(subject, subject.Context, null, null);
            }
        }
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
        lock (_attachedSubjects)
        {
            var touchedProxies = new HashSet<IInterceptorSubject>();
            var collectedSubjects = new HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)>();
            FindSubjectsInProperties(subject, collectedSubjects, touchedProxies);

            DetachFrom(subject, subject.Context, null, null);
            foreach (var child in collectedSubjects)
            {
                DetachFrom(child.subject, subject.Context, child.property, child.index);
            }
        }
    }

    private void AttachTo(IInterceptorSubject subject, IInterceptorSubjectContext context, PropertyReference? property, object? index)
    {
        // TODO(perf): Can maybe be improved
        _attachedSubjects.TryAdd(subject, []);
        if (_attachedSubjects[subject].Add(property))
        {
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
            var registryContext = new SubjectLifecycleChange(subject, property, index, count ?? 1);

            foreach (var handler in context.GetServices<ILifecycleHandler>())
            {
                handler.AttachSubject(registryContext);
            }

            if (subject is ILifecycleHandler lifecycleHandler)
            {
                lifecycleHandler.AttachSubject(registryContext);
            }

            foreach (var propertyName in subject.Properties.Keys)
            {
                subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
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
            }

            // TODO: Detach dynamically added properties as well
            foreach (var propertyName in subject.Properties.Keys)
            {
                subject.DetachSubjectProperty(new PropertyReference(subject, propertyName));
            }
            
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 0, (_, count) => (int)count! - 1) as int?;
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

    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var currentValue = context.CurrentValue;
        var result = next(context);
        var newValue = context.NewValue;
        
        context.Property.SetWriteTimestamp(SubjectMutationContext.GetCurrentTimestamp());
        
        if (!Equals(currentValue, newValue))
        {
            lock (_attachedSubjects)
            {
                var oldTouchedSubjects = new HashSet<IInterceptorSubject>();
                var oldCollectedSubjects = new HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)>();
                FindSubjectsInProperty(context.Property, currentValue, null, oldCollectedSubjects, oldTouchedSubjects);

                var newTouchedSubjects = new HashSet<IInterceptorSubject>();
                var newCollectedSubjects = new HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)>();
                FindSubjectsInProperty(context.Property, newValue, null, newCollectedSubjects, newTouchedSubjects);

                if (oldCollectedSubjects.Count != 0 || newCollectedSubjects.Count != 0)
                {
                    foreach (var d in oldCollectedSubjects
                        .Reverse()
                        .Where(u => !newTouchedSubjects.Contains(u.subject)))
                    {
                        DetachFrom(d.subject, context.Property.Subject.Context, d.property, d.index);
                    }

                    foreach (var d in newCollectedSubjects
                        .Where(u => !oldTouchedSubjects.Contains(u.Item1)))
                    {
                        AttachTo(d.subject, context.Property.Subject.Context, d.property, d.index);
                    }
                }
            }
        }

        return result;
    }

    private void FindSubjectsInProperties(IInterceptorSubject subject,
        HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject> touchedSubjects)
    {
        // TODO: Also scan dynamic properties if available (registry)

        foreach (var property in subject.Properties
            .Where(p => subject.GetPropertyReference(p.Key).Metadata.IsDerived() == false))
        {
            var childValue = property.Value.GetValue?.Invoke(subject);
            if (childValue is not null)
            {
                FindSubjectsInProperty(new PropertyReference(subject, property.Key), 
                    childValue, null, collectedSubjects, touchedSubjects);
            }
        }
    }

    private void FindSubjectsInProperty(PropertyReference property,
        object? value, object? index,
        HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject> touchedSubjects)
    {
        if (value is IReadOnlyDictionary<string, IInterceptorSubject?> dictionary)
        {
            foreach (var (key, item) in dictionary)
            {
                if (item is not null && touchedSubjects.Add(item))
                {
                    collectedSubjects.Add((item, property, key));
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var subject in collection.OfType<IInterceptorSubject>())
            {
                if (touchedSubjects.Add(subject))
                {
                    collectedSubjects.Add((subject, property, i));
                }

                i++;
            }
        }
        else if (value is IInterceptorSubject subject && touchedSubjects.Add(subject))
        {
            collectedSubjects.Add((subject, property, index));
        }
    }
}