namespace Namotion.Proxy.Lifecycle;

// TODO: Is this needed? Remove?

public static class AutomaticContextAssignmentHandlerExtensions
{
    private const string ParentsKey = "Namotion.Parents";

    public static void AddParent(this IProxy proxy, IProxy parent)
    {
        var parents = proxy.GetParents();
        parents.Add(parent);
    }

    public static void RemoveParent(this IProxy proxy, IProxy parent)
    {
        var parents = proxy.GetParents();
        parents.Remove(parent);
    }

    public static HashSet<IProxy> GetParents(this IProxy proxy)
    {
        return (HashSet<IProxy>)proxy.Data.GetOrAdd(ParentsKey, (_) => new HashSet<IProxy>())!;
    }
}
