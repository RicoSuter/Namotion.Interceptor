namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorderScope : IDisposable
{
    private readonly ReadPropertyRecorder _recorder;
    private readonly HashSet<PropertyReference> _properties = [];

    public ReadPropertyRecorderScope(ReadPropertyRecorder recorder)
    {
        _recorder = recorder;
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
        _properties.Clear();
        lock (typeof(ReadPropertyRecorder))
        {
            ReadPropertyRecorder.Scopes.Value?[_recorder]?.Remove(this);
        }
    }

    internal void AddProperty(PropertyReference property)
    {
        _properties.Add(property);
    }
}
