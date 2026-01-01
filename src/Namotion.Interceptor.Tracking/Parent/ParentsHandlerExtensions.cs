using System.Collections.Immutable;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "Namotion.Interceptor.Tracking.Parents";

    internal static void AddParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        subject.Data.AddOrUpdate(
            (null, ParentsKey),
            _ => ImmutableHashSet.Create(new SubjectParent(parent, index)),
            (_, existing) => ((ImmutableHashSet<SubjectParent>)existing!).Add(new SubjectParent(parent, index)));
    }

    internal static void RemoveParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        subject.Data.AddOrUpdate(
            (null, ParentsKey),
            _ => ImmutableHashSet<SubjectParent>.Empty,
            (_, existing) => ((ImmutableHashSet<SubjectParent>)existing!).Remove(new SubjectParent(parent, index)));
    }

    public static IReadOnlyCollection<SubjectParent> GetParents(this IInterceptorSubject subject)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var parents))
        {
            return (ImmutableHashSet<SubjectParent>)parents!;
        }
        return ImmutableHashSet<SubjectParent>.Empty;
    }
}
