using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

internal class DerivedPropertyChangeDetectionHandler : IProxyLifecycleHandler, IReadInterceptor, IWriteInterceptor
{
    private readonly IInterceptorCollection _interceptorCollection;

    [ThreadStatic]
    private static Stack<HashSet<PropertyReference>>? _currentTouchedProperties;
    
    public DerivedPropertyChangeDetectionHandler(IInterceptorCollection interceptorCollection)
    {
        _interceptorCollection = interceptorCollection;
    }

    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        foreach (var property in context.Proxy.Properties.Where(p => p.Value.IsDerived))
        {
            var propertyReference = new PropertyReference(context.Proxy, property.Key);

            TryStartRecordTouchedProperties();

            var result = property.Value.GetValue?.Invoke(context.Proxy);
            propertyReference.SetLastKnownValue(result);

            StoreRecordedTouchedProperties(propertyReference);
            TouchProperty(propertyReference);
        }
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
    }

    public object? ReadProperty(ReadPropertyInterception context, Func<ReadPropertyInterception, object?> next)
    {
        var result = next(context);
        TouchProperty(context.Property);
        return result;
    }

    public void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next)
    {
        next.Invoke(context);

        var usedByProperties = context.Property.GetUsedByProperties();
        if (usedByProperties.Count == 0) 
            return;
        
        lock (usedByProperties)
        {
            foreach (var usedByProperty in usedByProperties)
            {
                var oldValue = usedByProperty.GetLastKnownValue();

                TryStartRecordTouchedProperties();

                var newValue = usedByProperty
                    .Subject
                    .Properties[usedByProperty.Name]
                    .GetValue?
                    .Invoke(usedByProperty.Subject);

                StoreRecordedTouchedProperties(usedByProperty);
                TouchProperty(usedByProperty);

                usedByProperty.SetLastKnownValue(newValue);
                
                _interceptorCollection.SetProperty(usedByProperty.Subject, usedByProperty.Name, newValue, () => oldValue, delegate {});
            }
        }
    }

    private static void TryStartRecordTouchedProperties()
    {
        _currentTouchedProperties ??= new Stack<HashSet<PropertyReference>>();
        _currentTouchedProperties.Push([]);
    }

    private static void StoreRecordedTouchedProperties(PropertyReference property)
    {
        var newProperties = _currentTouchedProperties!.Pop();

        var previouslyRequiredProperties = property.GetRequiredProperties();
        foreach (var previouslyRequiredProperty in previouslyRequiredProperties.Except(newProperties))
        {
            var usedByProperties = previouslyRequiredProperty.GetUsedByProperties();
            lock (usedByProperties)
                usedByProperties.Remove(previouslyRequiredProperty);
        }

        property.SetRequiredProperties(newProperties);

        foreach (var newlyRequiredProperty in newProperties.Except(previouslyRequiredProperties))
        {
            var usedByProperties = newlyRequiredProperty.GetUsedByProperties();
            lock (usedByProperties)
                usedByProperties.Add(new PropertyReference(property.Subject, property.Name));
        }
    }

    private static void TouchProperty(PropertyReference property)
    {
        if (_currentTouchedProperties?.TryPeek(out var touchedProperties) == true)
        {
            touchedProperties.Add(property);
        }
        else
        {
            _currentTouchedProperties = null;
        }
    }
}
