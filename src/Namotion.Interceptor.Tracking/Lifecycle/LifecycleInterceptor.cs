using System.Collections;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor
{
    // TODO(perf): Profile and improve this class, high potential to improve

    private readonly IInterceptorSubjectContext _context;
    
    private const string ReferenceCountKey = "Namotion.ReferenceCount";
 
    private readonly HashSet<IInterceptorSubject> _attachedSubjects = []; // TODO: Use in locks only

    public LifecycleInterceptor(IInterceptorSubjectContext context)
    {
        _context = context;
    }

    public void AttachTo(IInterceptorSubject subject)
    {
        var touchedProxies = new HashSet<IInterceptorSubject>();
        var proxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
        FindSubjectsInProperties(subject, proxyProperties, touchedProxies);
        
        AttachTo(subject, null, null);
        foreach (var child in proxyProperties)
        {
            AttachTo(child.Item1, child.Item2, child.Item3);
        }
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
        var touchedProxies = new HashSet<IInterceptorSubject>();
        var proxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
        FindSubjectsInProperties(subject, proxyProperties, touchedProxies);
        
        foreach (var child in proxyProperties)
        {
            DetachFrom(child.Item1, child.Item2, child.Item3);
        }
        DetachFrom(subject, null, null);
    }

    private void AttachTo(IInterceptorSubject subject, PropertyReference? property, object? index)
    {
        if (_attachedSubjects.Add(subject))
        {
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
            var registryContext = new LifecycleContext(property, index, subject, count ?? 1);

            foreach (var handler in _context.GetServices<ILifecycleHandler>())
            {
                handler.Attach(registryContext);
            }
        }
    }

    private void DetachFrom(IInterceptorSubject subject, PropertyReference? property, object? index)
    {
        if (_attachedSubjects.Remove(subject))
        {
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 0, (_, count) => (int)count! - 1) as int?;
            var registryContext = new LifecycleContext(property, index, subject, count ?? 1);
       
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
            FindSubjects(context.Property, currentValue, null, oldProxies, oldProxyProperties);

            var newProxies = new HashSet<IInterceptorSubject>();
            var newProxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
            FindSubjects(context.Property, newValue, null, newProxies, newProxyProperties);

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

    private void FindSubjects(
        PropertyReference property,
        object? value, object? index,
        HashSet<IInterceptorSubject> touchedProxies,
        HashSet<(IInterceptorSubject, PropertyReference, object?)> result)
    {
        if (value is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                var dictionaryValue = dictionary[key];
                if (dictionaryValue is IInterceptorSubject proxy)
                {
                    FindSubjects(property, proxy, key, touchedProxies, result);
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var proxy in collection.OfType<IInterceptorSubject>())
            {
                FindSubjects(property, proxy, i, touchedProxies, result);
                i++;
            }
        }
        else if (value is IInterceptorSubject proxy)
        {
            result.Add((proxy, property, index));
            if (touchedProxies.Add(proxy))
            {
                FindSubjectsInProperties(proxy, result, touchedProxies);
            }
        }
    }

    private void FindSubjectsInProperties(IInterceptorSubject subject,
        HashSet<(IInterceptorSubject, PropertyReference, object?)> collectedSubjects,
        HashSet<IInterceptorSubject> touchedProxies)
    {
        foreach (var property in subject.Properties)
        {
            var childValue = property.Value.GetValue?.Invoke(subject);
            if (childValue is not null)
            {
                FindSubjects(new PropertyReference(subject, property.Key), childValue, null, touchedProxies, collectedSubjects);
            }
        }
    }
}