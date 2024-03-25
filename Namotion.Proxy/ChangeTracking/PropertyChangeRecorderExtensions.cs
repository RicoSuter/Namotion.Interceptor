namespace Namotion.Proxy.ChangeTracking;

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
