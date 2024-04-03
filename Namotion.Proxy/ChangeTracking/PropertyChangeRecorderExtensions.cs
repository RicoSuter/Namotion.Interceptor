namespace Namotion.Proxy.ChangeTracking;

public static class PropertyChangeRecorderExtensions
{
    public static PropertyChangeRecorderScope BeginReadPropertyRecording(this IProxyContext context)
    {
        ReadPropertyRecorder.Scopes.Value =
            ReadPropertyRecorder.Scopes.Value ??
            new Dictionary<IProxyContext, List<HashSet<ProxyPropertyReference>>>();

        var scope = new HashSet<ProxyPropertyReference>();
        ReadPropertyRecorder.Scopes.Value.TryAdd(context, new List<HashSet<ProxyPropertyReference>>());
        ReadPropertyRecorder.Scopes.Value[context].Add(scope);

        return new PropertyChangeRecorderScope(context, scope);
    }
}
