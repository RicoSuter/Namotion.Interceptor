using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

public class DetectDerivedPropertyChangesHandler : IProxyReadHandler
{
    private record struct TrackedProperty(IProxy Proxy, string PropertyName);

    [ThreadStatic]
    private static Stack<HashSet<TrackedProperty>>? _currentTouchedProperties;

    ///// <summary>
    ///// Gets the properties which are used to calculate the value of this derived property.
    ///// </summary>
    //[JsonIgnore]
    //public IReadOnlyCollection<TrackedProperty> RequiredProperties { get; internal set; } = ImmutableHashSet<TrackedProperty>.Empty;

    public object? GetProperty(ProxyReadHandlerContext context, Func<ProxyReadHandlerContext, object?> next)
    {
        TryStartRecordingTouchedProperties();
        var result = next(context);
        StoreTouchedProperties(context);
        return result;
    }

    public bool IsDerived => true;

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

            var previouslyRequiredProperties = context.Proxy.Data["RequiredProperties"] as HashSet<TrackedProperty>;
            if (previouslyRequiredProperties != null)
            {
                foreach (var previouslyRequiredProperty in previouslyRequiredProperties)
                {
                    if (!newProperties.Contains(previouslyRequiredProperty))
                    {
                        var usedByProperties = previouslyRequiredProperty.Proxy.Data.GetOrAdd("UsedByProperties", () => new HashSet<TrackedProperty>()) as HashSet<TrackedProperty>;
                        lock (usedByProperties!)
                            usedByProperties.Remove(previouslyRequiredProperty);
                    }
                }
            }

            context.Proxy.Data["RequiredProperties"] = newProperties;

            foreach (var newlyRequiredProperty in newProperties)
            {
                var usedByProperties = newlyRequiredProperty.Proxy.Data.GetOrAdd("UsedByProperties", () => new HashSet<TrackedProperty>()) as HashSet<TrackedProperty>;
                lock (usedByProperties!)
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
