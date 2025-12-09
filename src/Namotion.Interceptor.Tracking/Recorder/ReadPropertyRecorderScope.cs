using System.Collections.Concurrent;

namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// A scope for recording property reads.
/// Used by TrackingComponentBase for tracking which properties are accessed during rendering.
/// For Blazor components, explicit recording via RegisteredSubjectProperty.GetValueAndRecord()
/// is preferred since ambient context doesn't flow through RenderFragment execution.
/// </summary>
public class ReadPropertyRecorderScope : IDisposable
{
    private readonly ConcurrentDictionary<PropertyReference, bool> _properties;
    private volatile int _disposed;

    internal ReadPropertyRecorderScope(IInterceptorSubjectContext? context, ConcurrentDictionary<PropertyReference, bool>? properties)
    {
        _properties = properties ?? [];
        // Note: Do NOT clear here - the caller (TrackingComponentBase) manages clearing
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
        Interlocked.Exchange(ref _disposed, 1);
    }
}
