using Namotion.Interceptor;

namespace Namotion.Proxy.Lifecycle;

// TODO: Is this needed? Remove?

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "Namotion.Parents";

    public static void AddParent(this IInterceptorSubject subject, ProxyPropertyReference parent, object? index)
    {
        var parents = subject.GetParents();
        parents.Add(new ProxyParent(parent, index));
    }

    public static void RemoveParent(this IInterceptorSubject subject, ProxyPropertyReference parent, object? index)
    {
        var parents = subject.GetParents();
        parents.Remove(new ProxyParent(parent, index));
    }
    
    public static HashSet<ProxyParent> GetParents(this IInterceptorSubject subject)
    {
        return (HashSet<ProxyParent>)subject.Data.GetOrAdd(ParentsKey, (_) => new HashSet<ProxyParent>())!;
    }
}

public record struct ProxyParent(
    ProxyPropertyReference Property,
    object? Index)
{
}
