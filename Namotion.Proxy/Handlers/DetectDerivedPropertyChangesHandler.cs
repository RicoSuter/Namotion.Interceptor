using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

public record struct TrackedProperty(IProxy Proxy, string PropertyName);

public class DetectDerivedPropertyChangesHandler : IProxyReadHandler, IProxyWriteHandler
{
    [ThreadStatic]
    private static Stack<HashSet<TrackedProperty>>? _currentTouchedProperties;

    public bool IsDerived => true;

    public object? GetProperty(ProxyReadHandlerContext context, Func<ProxyReadHandlerContext, object?> next)
    {
        TryStartRecordingTouchedProperties();
        var result = next(context);
        StoreTouchedProperties(context);
        return result;
    }

    public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
    {
        next.Invoke(context);

        var usedByProperties = context.Proxy.GetUsedByProperties();
        if (usedByProperties.Any())
        {
            lock (usedByProperties)
            {
                foreach (var usedByProperty in usedByProperties)
                {
                    var changedContext = new ProxyChangedHandlerContext(context.Context, usedByProperty.Proxy, usedByProperty.PropertyName, null, null); // TODO: how to provide current and new value?
                    foreach (var handler in context.Context.GetHandlers<IProxyChangedHandler>())
                    {
                        handler.RaisePropertyChanged(changedContext);
                    }
                }
            }
        }
    }

    private void TryStartRecordingTouchedProperties()
    {
        if (IsDerived)
        {
            if (_currentTouchedProperties == null)
            {
                _currentTouchedProperties = new Stack<HashSet<TrackedProperty>>();
            }

            _currentTouchedProperties.Push(new HashSet<TrackedProperty>());
        }
    }

    private void StoreTouchedProperties(ProxyReadHandlerContext context)
    {
        if (IsDerived)
        {
            var newProperties = _currentTouchedProperties!.Pop();

            var previouslyRequiredProperties = context.Proxy.GetRequiredProperties();
            foreach (var previouslyRequiredProperty in previouslyRequiredProperties)
            {
                if (!newProperties.Contains(previouslyRequiredProperty))
                {
                    var usedByProperties = previouslyRequiredProperty.Proxy.GetUsedByProperties();
                    lock (usedByProperties)
                        usedByProperties.Remove(previouslyRequiredProperty);
                }
            }

            context.Proxy.SetRequiredProperties(newProperties);

            foreach (var newlyRequiredProperty in newProperties)
            {
                var usedByProperties = newlyRequiredProperty.Proxy.GetUsedByProperties();
                lock (usedByProperties)
                    usedByProperties.Add(new TrackedProperty(context.Proxy, context.PropertyName));
            }
        }

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
