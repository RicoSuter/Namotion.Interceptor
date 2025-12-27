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
        // TODO: Make thread safe!

        return (HashSet<SubjectParent>)subject.Data.GetOrAdd((null, ParentsKey), (_) => new HashSet<SubjectParent>())!;
    }

    /// <summary>
    /// Finds the nearest ancestor of type T by traversing the parent hierarchy.
    /// Checks the subject itself first, then traverses parents.
    /// </summary>
    /// <typeparam name="T">The type to find.</typeparam>
    /// <param name="subject">The subject to start from.</param>
    /// <returns>The nearest ancestor of type T, or default if not found.</returns>
    public static T? FindNearestAncestor<T>(this IInterceptorSubject subject)
        where T : class
    {
        // Check if the subject itself matches
        if (subject is T self)
        {
            return self;
        }

        // Traverse parents to find the nearest match
        var visited = new HashSet<IInterceptorSubject>();
        var current = subject;

        while (current != null)
        {
            if (!visited.Add(current))
            {
                break; // Avoid infinite loops
            }

            var parents = current.GetParents();
            if (parents.Count == 0)
            {
                break;
            }

            // Check each parent for the target type
            foreach (var parent in parents)
            {
                if (parent.Property.Subject is T match)
                {
                    return match;
                }
            }

            // Move to the first parent's subject for next iteration
            var firstParent = parents.FirstOrDefault();
            current = firstParent.Property.Subject != current ? firstParent.Property.Subject : null;
           
            // TODO: Handle multiple parents more intelligently if needed (use queue)
            // TODO: Replace with BFS when available in Namotion.Interceptor.Tracking (needs to be fixed in this PR)
        }

        return default;
    }
}