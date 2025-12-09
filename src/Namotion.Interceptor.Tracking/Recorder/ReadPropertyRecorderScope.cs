using System.Collections.Concurrent;

namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// A scope for recording property reads.
/// Each scope instance has its own properties dictionary, ensuring isolation
/// across different component instances/sessions.
/// </summary>
public class ReadPropertyRecorderScope : IDisposable
{
    private readonly IInterceptorSubjectContext? _context;
    private readonly ConcurrentDictionary<PropertyReference, bool> _properties;
    private volatile int _disposed;

    internal ReadPropertyRecorderScope(IInterceptorSubjectContext? context, ConcurrentDictionary<PropertyReference, bool>? properties)
    {
        _context = context;
        _properties = properties ?? [];
        // Note: Do NOT clear here - the caller (TrackingScope) manages clearing
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
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _context != null)
        {
            ReadPropertyRecorder.RemoveScope(_context, this);
        }
    }
}
