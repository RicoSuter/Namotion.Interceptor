namespace Namotion.Proxy.Lifecycle;

// TODO: Is this needed? Remove?

public static class AutomaticContextAssignmentHandlerExtensions
{
    private const string ParentsKey = "Namotion.Parents";

    public static void AddParent(this IProxy proxy, ProxyPropertyReference parent)
    {
        var parents = proxy.GetParents();
        parents.Add(parent);
    }

    public static void RemoveParent(this IProxy proxy, ProxyPropertyReference parent)
    {
        var parents = proxy.GetParents();
        parents.Remove(parent);
    }

    public static HashSet<ProxyPropertyReference> GetParents(this IProxy proxy)
    {
        return (HashSet<ProxyPropertyReference>)proxy.Data.GetOrAdd(ParentsKey, (_) => new HashSet<ProxyPropertyReference>())!;
    }
}
