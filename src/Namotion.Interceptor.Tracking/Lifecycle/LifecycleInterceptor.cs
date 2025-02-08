using System.Collections;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor
{
    private const string ReferenceCountKey = "Namotion.ReferenceCount";

    // TODO(perf): Profile and improve this class, high potential to improve

    private readonly IInterceptorSubjectContext _context;
    private readonly HashSet<(IInterceptorSubject, PropertyReference?)> _attachedSubjects = []; // TODO: Use in locks only

    public LifecycleInterceptor(IInterceptorSubjectContext context)
    {
        _context = context; // TODO: remove and use subject context instead?
    }

    public void AttachTo(IInterceptorSubject subject)
    {
        var touchedProxies = new HashSet<IInterceptorSubject>();
        var collectedSubjects = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
        FindSubjectsInProperties(subject, collectedSubjects, touchedProxies);

        foreach (var child in collectedSubjects)
        {
            AttachTo(child.Item1, child.Item2, child.Item3);
        }

        var alreadyAttachedOnProperty = _attachedSubjects.Any(s => s.Item1 == subject);
        if (!alreadyAttachedOnProperty)
        {
            AttachTo(subject, null, null);
        }
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
        var touchedProxies = new HashSet<IInterceptorSubject>();
        var collectedSubjects = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
        FindSubjectsInProperties(subject, collectedSubjects, touchedProxies);

        DetachFrom(subject, null, null);
        foreach (var child in collectedSubjects)
        {
            DetachFrom(child.Item1, child.Item2, child.Item3);
        }
    }

    private void AttachTo(IInterceptorSubject subject, PropertyReference? property, object? index)
    {
        if (_attachedSubjects.Add((subject, property)))
        {
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
            var registryContext = new LifecycleContext(subject, property, index, count ?? 1);

            foreach (var handler in _context.GetServices<ILifecycleHandler>())
            {
                handler.Attach(registryContext);
            }
        }
    }

    private void DetachFrom(IInterceptorSubject subject, PropertyReference? property, object? index)
    {
        if (_attachedSubjects.Remove((subject, property)))
        {
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 0, (_, count) => (int)count! - 1) as int?;
            var registryContext = new LifecycleContext(subject, property, index, count ?? 1);

            foreach (var handler in _context.GetServices<ILifecycleHandler>())
            {
                handler.Detach(registryContext);
            }
        }
    }

    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        var currentValue = context.CurrentValue;
        var result = next(context);
        var newValue = context.NewValue;

        if (!Equals(currentValue, newValue))
        {
            var oldProxies = new HashSet<IInterceptorSubject>();
            var oldProxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
            FindSubjectsInProperty(context.Property, currentValue, null, oldProxyProperties, oldProxies);

            var newProxies = new HashSet<IInterceptorSubject>();
            var newProxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
            FindSubjectsInProperty(context.Property, newValue, null, newProxyProperties, newProxies);

            if (oldProxyProperties.Count != 0 || newProxyProperties.Count != 0)
            {
                foreach (var d in oldProxyProperties
                             .Reverse()
                             .Where(u => !newProxies.Contains(u.Item1)))
                {
                    DetachFrom(d.Item1, d.Item2, d.Item3);
                }

                foreach (var d in newProxyProperties
                             .Where(u => !oldProxies.Contains(u.Item1)))
                {
                    AttachTo(d.Item1, d.Item2, d.Item3);
                }
            }
        }

        return result;
    }

    private void FindSubjectsInProperties(IInterceptorSubject subject,
        HashSet<(IInterceptorSubject, PropertyReference, object?)> collectedSubjects,
        HashSet<IInterceptorSubject> touchedSubjects)
    {
        foreach (var property in subject.Properties)
        {
            var childValue = property.Value.GetValue?.Invoke(subject);
            if (childValue is not null)
            {
                FindSubjectsInProperty(new PropertyReference(subject, property.Key), childValue, null, collectedSubjects, touchedSubjects);
            }
        }
    }

    private void FindSubjectsInProperty(PropertyReference property,
        object? value, object? index,
        HashSet<(IInterceptorSubject, PropertyReference, object?)> collectedSubjects,
        HashSet<IInterceptorSubject> touchedSubjects)
    {
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is IInterceptorSubject proxy)
                {
                    FindSubjectsInProperty(property, proxy, entry.Key, collectedSubjects, touchedSubjects);
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var proxy in collection.OfType<IInterceptorSubject>())
            {
                FindSubjectsInProperty(property, proxy, i, collectedSubjects, touchedSubjects);
                i++;
            }
        }
        else if (value is IInterceptorSubject subject && touchedSubjects.Add(subject))
        {
            FindSubjectsInProperties(subject, collectedSubjects, touchedSubjects);
            collectedSubjects.Add((subject, property, index));
        }
    }
}