using System.Collections;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private const string ReferenceCountKey = "Namotion.Interceptor.Tracking.ReferenceCount";
    
    private readonly Dictionary<IInterceptorSubject, HashSet<PropertyReference?>> _attachedSubjects = [];

    public void AttachTo(IInterceptorSubject subject)
    {
        lock (_attachedSubjects)
        {
            var collectedSubjects = new HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)>();
            FindSubjectsInProperties(subject, collectedSubjects, null);

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
            var collectedSubjects = new HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)>();
            FindSubjectsInProperties(subject, collectedSubjects, null);

            DetachFrom(subject, subject.Context, null, null);
            foreach (var child in collectedSubjects)
            {
                DetachFrom(child.subject, subject.Context, child.property, child.index);
            }
        }
    }

    private void AttachTo(IInterceptorSubject subject, IInterceptorSubjectContext context, PropertyReference? property, object? index)
    {
        _attachedSubjects.TryAdd(subject, []);
        if (_attachedSubjects[subject].Add(property))
        {
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
            var registryContext = new SubjectLifecycleChange(subject, property, index, count ?? 1);

            // keep original keys in case handlers add properties during attach (will be attached directly)
            var properties = subject.Properties.Keys;
            
            foreach (var handler in context.GetServices<ILifecycleHandler>())
            {
                handler.AttachSubject(registryContext);
            }

            if (subject is ILifecycleHandler lifecycleHandler)
            {
                lifecycleHandler.AttachSubject(registryContext);
            }
            
            foreach (var propertyName in properties)
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

    public void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next)
    {
        var currentValue = context.CurrentValue;
        next(ref context);
        var newValue = context.NewValue;
        
        context.Property.SetWriteTimestamp(SubjectMutationContext.GetCurrentTimestamp());
        
        if (!Equals(currentValue, newValue) &&
            (currentValue is IInterceptorSubject || 
             currentValue is ICollection || 
             currentValue is IDictionary ||
             newValue is IInterceptorSubject || 
             newValue is ICollection || 
             newValue is IDictionary))
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
                        .Where(u => !oldTouchedSubjects.Contains(u.subject)))
                    {
                        AttachTo(d.subject, context.Property.Subject.Context, d.property, d.index);
                    }
                }
            }
        }
    }

    private void FindSubjectsInProperties(IInterceptorSubject subject,
        HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        // TODO: Also scan dynamic properties if available (registry), is this needed?
        
        foreach (var property in subject.Properties)
        {
            if (property.Value.IsDerived)
                continue;

            var childValue = property.Value.GetValue?.Invoke(subject);
            if (childValue is not null)
            {
                FindSubjectsInProperty(new PropertyReference(subject, property.Key), 
                    childValue, null, collectedSubjects, touchedSubjects);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FindSubjectsInProperty(PropertyReference property,
        object? value, object? index,
        HashSet<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        if (value is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                var item = dictionary[key];
                if (item is IInterceptorSubject subjectItem && touchedSubjects?.Add(subjectItem) != false)
                {
                    collectedSubjects.Add((subjectItem, property, key));
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var item in collection)
            {
                if (item is IInterceptorSubject subject && touchedSubjects?.Add(subject) != false)
                {
                    collectedSubjects.Add((subject, property, i));
                }

                i++;
            }
        }
        else if (value is IInterceptorSubject subject && touchedSubjects?.Add(subject) != false)
        {
            collectedSubjects.Add((subject, property, index));
        }
    }
}