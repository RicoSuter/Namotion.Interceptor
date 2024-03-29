namespace Namotion.Proxy.ChangeTracking;

public static class PropertyChangeRecorderExtensions
{
    public static PropertyChangeRecorderScope BeginPropertyChangedRecording(this IProxyContext context)
    {
        PropertyChangeRecorder.Scopes.Value =
            PropertyChangeRecorder.Scopes.Value ??
            new Dictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>();

        var scope = new HashSet<ProxyPropertyReference>();
        PropertyChangeRecorder.Scopes.Value.TryAdd(context, new List<HashSet<ProxyPropertyReference>>());
        PropertyChangeRecorder.Scopes.Value[context].Add(scope);

        return new PropertyChangeRecorderScope(context, scope);
    }
}
