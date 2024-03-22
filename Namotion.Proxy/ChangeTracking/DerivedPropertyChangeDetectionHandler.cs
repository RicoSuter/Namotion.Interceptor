using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

/// <summary>
/// Should be used with <see cref="InitiallyLoadDerivedPropertiesHandler"/> so that dependencies are initially set up.
/// </summary>
internal class DerivedPropertyChangeDetectionHandler : IProxyReadHandler, IProxyWriteHandler
{
    [ThreadStatic]
    private static Stack<HashSet<ProxyPropertyReference>>? _currentTouchedProperties;

    public object? GetProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next)
    {
        if (context.Proxy.Properties[context.PropertyName].IsDerived)
        {
            TryStartRecordTouchedProperties();

            var result = next(context);
            context.Proxy.SetLastKnownValue(context.PropertyName, result);

            StoreRecordedTouchedProperties(context);
            TouchProperty(context);

            return result;
        }
        else
        {
            var result = next(context);
            TouchProperty(context);
            return result;
        }
    }

    public void SetProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next)
    {
        next.Invoke(context);

        var usedByProperties = context.Proxy.GetUsedByProperties(context.PropertyName);
        if (usedByProperties.Any())
        {
            lock (usedByProperties)
            {
                foreach (var usedByProperty in usedByProperties)
                {
                    var oldValue = usedByProperty.Proxy.GetLastKnownValue(usedByProperty.PropertyName);
                    var newValue = usedByProperty.Proxy
                        .Properties[usedByProperty.PropertyName]
                        .ReadValue(usedByProperty.Proxy);

                    var changedContext = new ProxyChangedContext(context.Context, usedByProperty.Proxy, usedByProperty.PropertyName, oldValue, newValue);
                    foreach (var handler in context.Context.GetHandlers<IProxyChangedHandler>())
                    {
                        handler.RaisePropertyChanged(changedContext);
                    }
                }
            }
        }
    }

    private void TryStartRecordTouchedProperties()
    {
        if (_currentTouchedProperties == null)
        {
            _currentTouchedProperties = new Stack<HashSet<ProxyPropertyReference>>();
        }

        _currentTouchedProperties.Push(new HashSet<ProxyPropertyReference>());
    }

    private void StoreRecordedTouchedProperties(ReadProxyPropertyContext context)
    {
        var newProperties = _currentTouchedProperties!.Pop();

        var previouslyRequiredProperties = context.Proxy.GetRequiredProperties(context.PropertyName);
        foreach (var previouslyRequiredProperty in previouslyRequiredProperties)
        {
            if (!newProperties.Contains(previouslyRequiredProperty))
            {
                var usedByProperties = previouslyRequiredProperty.Proxy.GetUsedByProperties(previouslyRequiredProperty.PropertyName);
                lock (usedByProperties)
                    usedByProperties.Remove(previouslyRequiredProperty);
            }
        }

        context.Proxy.SetRequiredProperties(context.PropertyName, newProperties);

        foreach (var newlyRequiredProperty in newProperties)
        {
            var usedByProperties = newlyRequiredProperty.Proxy.GetUsedByProperties(newlyRequiredProperty.PropertyName);
            lock (usedByProperties)
                usedByProperties.Add(new ProxyPropertyReference(context.Proxy, context.PropertyName));
        }
    }

    private void TouchProperty(ReadProxyPropertyContext context)
    {
        if (_currentTouchedProperties?.TryPeek(out var touchedProperties) == true)
        {
            touchedProperties.Add(new ProxyPropertyReference(context.Proxy, context.PropertyName));
        }
        else
        {
            _currentTouchedProperties = null;
        }
    }
}
