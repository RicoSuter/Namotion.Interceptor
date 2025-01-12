using Namotion.Interceptor;

namespace Namotion.Proxy.ChangeTracking;

public class ReadPropertyRecorderScope : IDisposable
{
    private readonly IInterceptor _context;
    private readonly HashSet<PropertyReference> _properties;

    public ReadPropertyRecorderScope(IInterceptor context, HashSet<PropertyReference> properties)
    {
        _context = context;
        _properties = properties;
    }

    public PropertyReference[] Properties
    {
        get
        {
            lock (typeof(ReadPropertyRecorder))
            {
                return _properties.ToArray();
            }
        }
    }

    public PropertyReference[] GetPropertiesAndReset()
    {
        lock (typeof(ReadPropertyRecorder))
        {
            var properties = _properties.ToArray();
            _properties.Clear();
            return properties;
        }
    }

    public PropertyReference[] GetPropertiesAndDispose()
    {
        lock (typeof(ReadPropertyRecorder))
        {
            var properties = _properties.ToArray();
            Dispose();
            return properties;
        }
    }

    public void Dispose()
    {
        lock (typeof(ReadPropertyRecorder))
        {
            ReadPropertyRecorder.Scopes.Value?[_context]?.Remove(_properties);
        }
    }
}
