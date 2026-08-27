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
        // Reference equality; see OwnershipGraph for why graph membership is identity.
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
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
    /// asks pays nothing. An unattached subject returns empty, which is the answer rather than a
    /// stand-in for one: no edge can point at it, because an attached parent would have pulled it
    /// into the context.
    ///
    /// The order of the entries is unspecified and history-dependent: only the set of occurrences,
    /// each with its property and its index or key, is meaningful.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The subject is attached to a context that has
    /// no <see cref="LifecycleInterceptor"/>, which cannot answer the question rather than
    /// answering it with an empty result.</exception>
    public static ImmutableArray<SubjectParent> GetParents(this IInterceptorSubject subject)
    {
        var context = subject.TryGetContext();
        if (context is null)
        {
            return [];
        }

        return context.TryGetLifecycleInterceptor()?.GetParents(subject)
            ?? throw new InvalidOperationException(
                $"LifecycleInterceptor not configured for the context of '{subject.GetType().Name}'. " +
                "Call WithLifecycle() on the context to enable parent tracking.");
    }
}
