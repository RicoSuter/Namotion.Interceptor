using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

/// <summary>
/// Should be used with <see cref="InitiallyLoadDerivedPropertiesHandler"/> so that dependencies are initially set up.
/// </summary>
internal class DerivedPropertyChangeDetectionHandler : IProxyReadHandler, IProxyWriteHandler
{
    private readonly Lazy<IProxyDerivedPropertyChangedHandler[]> _handlers;

    [ThreadStatic]
    private static Stack<HashSet<ProxyPropertyReference>>? _currentTouchedProperties;

    public DerivedPropertyChangeDetectionHandler(Lazy<IProxyDerivedPropertyChangedHandler[]> handlers)
    {
        _handlers = handlers;
    }

    public object? ReadProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next)
    {
        if (context.Property.Metadata.IsDerived)
        {
            TryStartRecordTouchedProperties();

            var result = next(context);

            context.Property.SetLastKnownValue(result);

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

    public void WriteProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next)
    {
        next.Invoke(context);

        var usedByProperties = context.Property.GetUsedByProperties();
        if (usedByProperties.Any())
        {
            lock (usedByProperties)
            {
                foreach (var usedByProperty in usedByProperties)
                {
                    var oldValue = usedByProperty.GetLastKnownValue();
                    var newValue = usedByProperty.Proxy
                        .Properties[usedByProperty.Name]
                        .GetValue?
                        .Invoke(usedByProperty.Proxy);

                    var changedContext = new ProxyPropertyChanged(usedByProperty, oldValue, newValue, context.Context);
                    foreach (var handler in _handlers.Value)
                    {
                        handler.OnDerivedPropertyChanged(changedContext);
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

        var previouslyRequiredProperties = context.Property.GetRequiredProperties();
        foreach (var previouslyRequiredProperty in previouslyRequiredProperties)
        {
            if (!newProperties.Contains(previouslyRequiredProperty))
            {
                var usedByProperties = previouslyRequiredProperty.GetUsedByProperties();
                lock (usedByProperties)
                    usedByProperties.Remove(previouslyRequiredProperty);
            }
        }

        context.Property.SetRequiredProperties(newProperties);

        foreach (var newlyRequiredProperty in newProperties)
        {
            var usedByProperties = newlyRequiredProperty.GetUsedByProperties();
            lock (usedByProperties)
                usedByProperties.Add(new ProxyPropertyReference(context.Property.Proxy, context.Property.Name));
        }
    }

    private void TouchProperty(ReadProxyPropertyContext context)
    {
        if (_currentTouchedProperties?.TryPeek(out var touchedProperties) == true)
        {
            touchedProperties.Add(new ProxyPropertyReference(context.Property.Proxy, context.Property.Name));
        }
        else
        {
            _currentTouchedProperties = null;
        }
    }
}
