namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "Namotion.Parents";

    internal static void AddParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        var parents = subject.GetParents();
        parents.Add(new SubjectParent(parent, index));
    }

    internal static void RemoveParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        var parents = subject.GetParents();
        parents.Remove(new SubjectParent(parent, index));
    }
    
    public static HashSet<SubjectParent> GetParents(this IInterceptorSubject subject)
    {
        return (HashSet<SubjectParent>)subject.Data.GetOrAdd(ParentsKey, (_) => new HashSet<SubjectParent>())!;
    }
}