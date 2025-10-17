using System.Collections.Concurrent;

namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorderScope : IDisposable
{
    private readonly ConcurrentDictionary<PropertyReference, bool> _properties;
    private volatile int _disposed;
    
    internal ReadPropertyRecorderScope(ConcurrentDictionary<PropertyReference, bool>? properties)
    {
        _properties = properties ?? [];
        _properties.Clear();
    }

    internal void AddProperty(PropertyReference property)
    {
        if (_disposed == 0)
        {
            _properties.TryAdd(property, false);
        }
    }

    /// <summary>
    /// Gets the recorded properties and disposes the scope.
    /// </summary>
    public ICollection<PropertyReference> GetPropertiesAndDispose()
    {
        Dispose();
        return _properties.Keys;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReadPropertyRecorder.Scopes.Value?.TryRemove(this, out _);
        }
    }
}
