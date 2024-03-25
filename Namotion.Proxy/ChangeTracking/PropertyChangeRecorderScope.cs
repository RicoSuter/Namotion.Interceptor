namespace Namotion.Proxy.ChangeTracking;

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
