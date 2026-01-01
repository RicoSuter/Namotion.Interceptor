using System.Collections;
using System.Collections.Concurrent;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "Namotion.Interceptor.Tracking.Parents";

    internal static void AddParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        var parents = (ParentsSet)subject.Data.GetOrAdd((null, ParentsKey), _ => new ParentsSet())!;
        parents.Add(new SubjectParent(parent, index));
    }

    internal static void RemoveParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var existing))
        {
            ((ParentsSet)existing!).Remove(new SubjectParent(parent, index));
        }
    }

    public static IReadOnlyCollection<SubjectParent> GetParents(this IInterceptorSubject subject)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var parents))
        {
            return (ParentsSet)parents!;
        }
        return [];
    }
    
    /// <summary>
    /// Thread-safe collection for storing parent references using ConcurrentDictionary.
    /// </summary>
    private sealed class ParentsSet : IReadOnlyCollection<SubjectParent>
    {
        private readonly ConcurrentDictionary<SubjectParent, byte> _dict = new();

        public int Count => _dict.Count;

        public bool Add(SubjectParent parent) => _dict.TryAdd(parent, 0);

        public bool Remove(SubjectParent parent) => _dict.TryRemove(parent, out _);

        public IEnumerator<SubjectParent> GetEnumerator() => _dict.Keys.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
