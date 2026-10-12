using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Services;

internal static class SubjectScopeFinder
{
    private const int MaximumSingleParentDepth = 64;

    /// <summary>
    /// Returns the subject itself when it is a <typeparamref name="T"/>, otherwise its nearest parent that is one, or null.
    /// </summary>
    public static T? FindNearest<T>(IInterceptorSubject subject)
        where T : class
    {
        // The usual chain has one parent per subject and is walked without an allocation. The search that
        // handles several parents and cycles allocates, so it only runs when the chain branches or is
        // longer than any real nesting.
        var current = subject;
        for (var depth = 0; depth < MaximumSingleParentDepth; depth++)
        {
            if (current is T scope)
            {
                return scope;
            }

            var parents = current.GetParents();
            if (parents.Length == 0)
            {
                return null;
            }

            if (parents.Length > 1)
            {
                break;
            }

            current = parents[0].Property.Subject;
        }

        return current as T ?? current.TryGetFirstParent<T>();
    }
}
