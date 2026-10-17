using System.Collections.Concurrent;

namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// A scope for recording property reads.
/// Each scope instance has its own properties dictionary, ensuring isolation
/// across different component instances/sessions.
/// Uses AsyncLocal-based storage (via ReadPropertyRecorder) for proper async context isolation.
/// </summary>
public sealed class ReadPropertyRecorderScope : IDisposable
{
    private readonly ConcurrentDictionary<PropertyReference, bool> _properties;

    // The list of the async context that started the scope, which Dispose must leave even when it runs in another one.
    private readonly List<ReadPropertyRecorderScope> _scopes;
    private volatile int _disposed;

    internal ReadPropertyRecorderScope(ConcurrentDictionary<PropertyReference, bool>? properties, List<ReadPropertyRecorderScope> scopes)
    {
        _properties = properties ?? [];
        _scopes = scopes;
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
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_scopes)
            {
                _scopes.Remove(this);
            }
        }
    }
}
