using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

// experimental
// TODO: Add lots of tests!

public static class PropertyChangeRecorderExtensions
{
    public static PropertyChangeRecorderScope BeginPropertyChangedRecording(this IProxyContext context)
    {
        PropertyChangeRecorder._scopes.Value =
            PropertyChangeRecorder._scopes.Value ??
            new Dictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>();

        var scope = new HashSet<ProxyPropertyReference>();
        PropertyChangeRecorder._scopes.Value.TryAdd(context, new List<HashSet<ProxyPropertyReference>>());
        PropertyChangeRecorder._scopes.Value[context].Add(scope);

        return new PropertyChangeRecorderScope(context, scope);
    }
}

public class PropertyChangeRecorderScope : IDisposable
{
    private readonly IProxyContext _context;
    private readonly HashSet<ProxyPropertyReference> _properties;

    public PropertyChangeRecorderScope(IProxyContext context, HashSet<ProxyPropertyReference> properties)
    {
        _context = context;
        _properties = properties;
    }

    public ProxyPropertyReference[] Properties
    {
        get
        {
            lock (typeof(PropertyChangeRecorder))
            {
                return _properties.ToArray();
            }
        }
    }

    public ProxyPropertyReference[] GetPropertiesAndReset()
    {
        lock (typeof(PropertyChangeRecorder))
        {
            var properties = _properties.ToArray();
            _properties.Clear();
            return properties;
        }
    }

    public ProxyPropertyReference[] GetPropertiesAndDispose()
    {
        lock (typeof(PropertyChangeRecorder))
        {
            var properties = _properties.ToArray();
            Dispose();
            return properties;
        }
    }

    public void Dispose()
    {
        lock (typeof(PropertyChangeRecorder))
            PropertyChangeRecorder._scopes.Value?[_context]?.Remove(_properties);
    }
}

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
                        scope.Add(new ProxyPropertyReference(context.Proxy, context.PropertyName));
                    }
                }
            }
        }

        return next(context);
    }
}
