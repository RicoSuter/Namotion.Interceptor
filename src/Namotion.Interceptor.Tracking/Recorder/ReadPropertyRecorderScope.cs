namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorderScope : IDisposable
{
    private readonly HashSet<PropertyReference> _properties;
    private volatile int _disposed;

    public ReadPropertyRecorderScope(HashSet<PropertyReference>? properties)
    {
        _properties = properties ?? [];
        _properties.Clear();
    }

    /// <summary>
    /// Gets whether this scope has been disposed.
    /// </summary>
    internal bool IsDisposed => _disposed != 0;

    /// <summary>
    /// Gets the recorded properties and disposes the scope.
    /// </summary>
    public HashSet<PropertyReference> GetPropertiesAndDispose()
    {
        Dispose();
        lock (this)
        {
            return _properties;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    internal void AddProperty(PropertyReference property)
    {
        if (_disposed == 0)
        {
            lock (this)
            {
                _properties.Add(property);
            }
        }
    }
}
