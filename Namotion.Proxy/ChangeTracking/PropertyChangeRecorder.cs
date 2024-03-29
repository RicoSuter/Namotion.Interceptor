using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

// experimental
// TODO: Add lots of tests!

internal class PropertyChangeRecorder : IProxyReadHandler
{
    internal static AsyncLocal<IDictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>> Scopes { get; } = new();

    public object? GetProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next)
    {
        if (Scopes.Value is not null)
        {
            lock (typeof(PropertyChangeRecorder))
            {
                if (Scopes.Value is not null &&
                    Scopes.Value.TryGetValue(context.Context, out var scopes))
                {
                    foreach (var scope in scopes)
                    {
                        scope.Add(context.Property);
                    }
                }
            }
        }

        return next(context);
    }
}
