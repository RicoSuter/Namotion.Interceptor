using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

// experimental
// TODO: Add lots of tests!

internal class PropertyChangeRecorder : IProxyReadHandler
{
    internal static AsyncLocal<IDictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>> _scopes = 
        new AsyncLocal<IDictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>>();

    public object? GetProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next)
    {
        if (_scopes.Value is not null)
        {
            lock (typeof(PropertyChangeRecorder))
            {
                if (_scopes.Value is not null &&
                    _scopes.Value.TryGetValue(context.Context, out var scopes))
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
