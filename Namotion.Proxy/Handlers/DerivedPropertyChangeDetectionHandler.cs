using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

public record struct TrackedProperty(IProxy Proxy, string PropertyName);

/// <summary>
/// Should be used with <see cref="InitiallyLoadDerivedPropertiesHandler"/> so that dependencies are initially set up.
/// </summary>
internal class DerivedPropertyChangeDetectionHandler : IProxyReadHandler, IProxyWriteHandler
{
    [ThreadStatic]
    private static Stack<HashSet<TrackedProperty>>? _currentTouchedProperties;

    public object? GetProperty(ProxyReadHandlerContext context, Func<ProxyReadHandlerContext, object?> next)
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

    public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
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

                    var changedContext = new ProxyChangedHandlerContext(context.Context, usedByProperty.Proxy, usedByProperty.PropertyName, oldValue, newValue);
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
            _currentTouchedProperties = new Stack<HashSet<TrackedProperty>>();
        }

        _currentTouchedProperties.Push(new HashSet<TrackedProperty>());
    }

    private void StoreRecordedTouchedProperties(ProxyReadHandlerContext context)
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
                usedByProperties.Add(new TrackedProperty(context.Proxy, context.PropertyName));
        }
    }

    private void TouchProperty(ProxyReadHandlerContext context)
    {
        if (_currentTouchedProperties?.TryPeek(out var touchedProperties) == true)
        {
            touchedProperties.Add(new TrackedProperty(context.Proxy, context.PropertyName));
        }
        else
        {
            _currentTouchedProperties = null;
        }
    }
}
