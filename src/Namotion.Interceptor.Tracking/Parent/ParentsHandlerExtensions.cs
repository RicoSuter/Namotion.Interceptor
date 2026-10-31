using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    /// <summary>
    /// Tries to find the nearest parent of the specified type by traversing the parent hierarchy.
    /// The subject itself is never returned. Returns null if not found instead of throwing.
    /// </summary>
    public static TRoot? TryGetFirstParent<TRoot>(this IInterceptorSubject subject)
        where TRoot : class
    {
        // The subjects found so far, in the order they were found: the queue of the breadth first search
        // and its visited set in one. It lives on the stack, and a search that finds more subjects than
        // it holds continues on the heap.
        var buffer = new InlineArray16<IInterceptorSubject>();
        Span<IInterceptorSubject> found = buffer;
        found[0] = subject;
        var count = 1;

        for (var next = 0; next < count; next++)
        {
            var parents = found[next].GetParents();
            for (var i = 0; i < parents.Length; i++)
            {
                // An entry without a subject does not come from tracking. It is skipped instead of ending
                // the search in an exception.
                var parent = parents[i].Property.Subject;
                if (parent is null || Contains(found[..count], parent))
                {
                    continue;
                }

                if (parent is TRoot root)
                {
                    return root;
                }

                if (count == found.Length)
                {
                    return FindFirstParentOnHeap<TRoot>(found, next, parents, i);
                }

                found[count++] = parent;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the subject itself when it is of the specified type, otherwise its nearest parent of that
    /// type. Returns null if not found instead of throwing.
    /// </summary>
    public static TRoot? TryGetFirstParentOrSelf<TRoot>(this IInterceptorSubject subject)
        where TRoot : class
    {
        return subject as TRoot ?? subject.TryGetFirstParent<TRoot>();
    }

    private static bool Contains(ReadOnlySpan<IInterceptorSubject> found, IInterceptorSubject subject)
    {
        foreach (var foundSubject in found)
        {
            if (ReferenceEquals(foundSubject, subject))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Continues the search of <see cref="TryGetFirstParent{TRoot}"/> when its buffer is full: at the parent
    /// <paramref name="parentIndex"/> of the subject <paramref name="next"/> in <paramref name="found"/>.
    /// </summary>
    // Kept out of line: inlined into a hot caller, it made searches past the buffer about 10 % slower.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TRoot? FindFirstParentOnHeap<TRoot>(
        ReadOnlySpan<IInterceptorSubject> found, int next, ImmutableArray<SubjectParent> parents, int parentIndex)
        where TRoot : class
    {
        var visited = new HashSet<IInterceptorSubject>(found.Length + 1, ReferenceEqualityComparer.Instance);
        var queue = new Queue<IInterceptorSubject>();
        for (var i = 0; i < found.Length; i++)
        {
            visited.Add(found[i]);
            if (i > next)
            {
                queue.Enqueue(found[i]);
            }
        }

        while (true)
        {
            if (EnqueueNewParents<TRoot>(parents, parentIndex, visited, queue) is { } match)
            {
                return match;
            }

            if (!queue.TryDequeue(out var current))
            {
                return null;
            }

            parents = current.GetParents();
            parentIndex = 0;
        }
    }

    private static TRoot? EnqueueNewParents<TRoot>(
        ImmutableArray<SubjectParent> parents, int start, HashSet<IInterceptorSubject> visited, Queue<IInterceptorSubject> queue)
        where TRoot : class
    {
        for (var i = start; i < parents.Length; i++)
        {
            var parent = parents[i].Property.Subject;
            if (parent is null || !visited.Add(parent))
            {
                continue;
            }

            if (parent is TRoot root)
            {
                return root;
            }

            queue.Enqueue(parent);
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
    /// into the context. The same holds on a context with no lifecycle, where the only subject that
    /// can be attached is one anchored to that context directly: nothing propagates the context
    /// along an edge, and a lifecycle cannot be registered behind an attach.
    ///
    /// An empty result is not a test for "this subject is a root". It also answers for an
    /// unattached subject, and for a subject inside its own release, because the release drops the
    /// ownership record before it runs any detach callback, so a getter evaluated from one sees no
    /// parents while the subject is still attached. Decide root-ness from the anchor instead, with
    /// a non-None <see cref="Interceptors.IInterceptorExecutor.AttachmentAnchor"/>.
    ///
    /// The order of the entries is unspecified and history-dependent: only the set of occurrences,
    /// each with its property and its index or key, is meaningful.
    /// </remarks>
    public static ImmutableArray<SubjectParent> GetParents(this IInterceptorSubject subject)
    {
        return subject.TryGetContext()?.TryGetLifecycleInterceptor()?.GetParents(subject) ?? [];
    }
}
