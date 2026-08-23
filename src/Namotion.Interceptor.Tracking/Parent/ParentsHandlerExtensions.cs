using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    /// <summary>
    /// Tries to find the first parent of the specified type by traversing the parent hierarchy.
    /// Returns null if not found instead of throwing.
    /// </summary>
    public static TRoot? TryGetFirstParent<TRoot>(this IInterceptorSubject subject)
        where TRoot : class
    {
        var visited = new HashSet<IInterceptorSubject>();
        var queue = new Queue<IInterceptorSubject>();
        queue.Enqueue(subject);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!visited.Add(current))
            {
                continue;
            }

            if (current is TRoot root && !ReferenceEquals(current, subject))
            {
                return root;
            }

            foreach (var parent in current.GetParents())
            {
                if (!parent.Equals(default))
                {
                    queue.Enqueue(parent.Property.Subject);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the occurrence-aware parents of the subject: one entry per structural edge that
    /// references it, carrying the referencing property and the collection index or dictionary key
    /// of that occurrence. A subject listed twice in one collection therefore has two entries.
    /// </summary>
    /// <remarks>
    /// The result is an immutable snapshot published by the built-in lifecycle, which is its single
    /// writer. Reading it takes no lock, which is required rather than an optimization: source scope
    /// walks call this while holding their own lock and are themselves called from inside the
    /// lifecycle lock.
    ///
    /// The first call on a subject activates parent publication for it, so a consumer that never
    /// asks pays nothing. An unattached subject, and a subject in a context using another lifecycle
    /// implementation, return empty.
    ///
    /// The order of the entries is unspecified and history-dependent: only the set of occurrences,
    /// each with its property and its index or key, is meaningful.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The subject's context resolves more than one
    /// built-in lifecycle, which happens when two contexts that each configure Tracking are
    /// composed. A subject belongs to exactly one context's graph, so that configuration has no
    /// answer.</exception>
    public static ImmutableArray<SubjectParent> GetParents(this IInterceptorSubject subject)
    {
        return subject.TryGetContext()?.TryGetLifecycleInterceptor()?.GetParents(subject) ?? [];
    }
}
