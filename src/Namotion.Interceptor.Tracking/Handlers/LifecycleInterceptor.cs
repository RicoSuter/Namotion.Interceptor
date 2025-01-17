using System.Collections;
using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Handlers;

public class LifecycleInterceptor : IWriteInterceptor
{
    // TODO(perf): Profile and improve this class, high potential to improve
    
    private const string ReferenceCountKey = "Namotion.ReferenceCount";
 
    private readonly ILifecycleHandler[] _handlers;
    private readonly HashSet<IInterceptorSubject> _attachedSubjects = []; // TODO: Use in locks only

    public LifecycleInterceptor(IEnumerable<ILifecycleHandler> handlers)
    {
        _handlers = handlers.ToArray();
    }

    public void AttachTo(IInterceptorSubject subject)
    {
        var touchedProxies = new HashSet<IInterceptorSubject>();
        var proxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
        FindProxiesInProperties(subject, touchedProxies, proxyProperties);
        
        AttachTo(null, subject, null);
        foreach (var child in proxyProperties)
        {
            AttachTo(child.Item2, child.Item1, child.Item3);
        }
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
        var touchedProxies = new HashSet<IInterceptorSubject>();
        var proxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
        FindProxiesInProperties(subject, touchedProxies, proxyProperties);
        
        foreach (var child in proxyProperties)
        {
            DetachFrom(child.Item2, child.Item1, child.Item3);
        }
        DetachFrom(null, subject, null);
    }

    private void AttachTo(PropertyReference? property, IInterceptorSubject subject, object? index)
    {
        if (_attachedSubjects.Add(subject))
        {
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
            var registryContext = new LifecycleContext(property, index, subject, count ?? 1);

            foreach (var handler in _handlers)
            {
                handler.Attach(registryContext);
            }
        }
    }

    private void DetachFrom(PropertyReference? property, IInterceptorSubject subject, object? index)
    {
        if (_attachedSubjects.Remove(subject))
        {
            var count = subject.Data.AddOrUpdate(ReferenceCountKey, 0, (_, count) => (int)count! - 1) as int?;
            var registryContext = new LifecycleContext(property, index, subject, count ?? 1);
       
            foreach (var handler in _handlers)
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
            FindProxies(context.Property, currentValue, null, oldProxies, oldProxyProperties);

            var newProxies = new HashSet<IInterceptorSubject>();
            var newProxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
            FindProxies(context.Property, newValue, null, newProxies, newProxyProperties);

            if (oldProxyProperties.Count != 0 || newProxyProperties.Count != 0)
            {
                foreach (var d in oldProxyProperties
                    .Reverse()
                    .Where(u => !newProxies.Contains(u.Item1)))
                {
                    DetachFrom(d.Item2, d.Item1, d.Item3);
                }

                foreach (var d in newProxyProperties
                    .Where(u => !oldProxies.Contains(u.Item1)))
                {
                    AttachTo(d.Item2, d.Item1, d.Item3);
                }
            }
        }

        return result;
    }

    private void FindProxies(
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
                    FindProxies(property, proxy, key, touchedProxies, result);
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var proxy in collection.OfType<IInterceptorSubject>())
            {
                FindProxies(property, proxy, i, touchedProxies, result);
                i++;
            }
        }
        else if (value is IInterceptorSubject proxy)
        {
            result.Add((proxy, property, index));
            if (touchedProxies.Add(proxy))
            {
                FindProxiesInProperties(proxy, touchedProxies, result);
            }
        }
    }

    private void FindProxiesInProperties(IInterceptorSubject subject,
        HashSet<IInterceptorSubject> touchedProxies,
        HashSet<(IInterceptorSubject, PropertyReference, object?)> result)
    {
        foreach (var property in subject.Properties)
        {
            var childValue = property.Value.GetValue?.Invoke(subject);
            if (childValue is not null)
            {
                FindProxies(new PropertyReference(subject, property.Key), childValue, null, touchedProxies, result);
            }
        }
    }
}