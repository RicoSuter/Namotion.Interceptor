using System.Reflection;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Reads <c>InterceptorSubjectContext._usedByContexts</c>, the reverse registration set that #207
/// reports growing without bound. Private with no accessor, so the leak is only observable by
/// reflection. Modelled on Namotion.Interceptor.Tests/Context/ContextStateReflection.cs: the lookup
/// raises with the field name, so a rename fails with the field to fix rather than a null reference.
/// </summary>
internal static class UsedByContextsProbe
{
    private static readonly FieldInfo UsedByContextsField = typeof(InterceptorSubjectContext)
        .GetField("_usedByContexts", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("InterceptorSubjectContext._usedByContexts was renamed, the leak tests need updating.");

    /// <summary>Returns how many contexts are registered as resolving through the given context.</summary>
    internal static int Count(IInterceptorSubjectContext context)
    {
        var set = UsedByContextsField.GetValue(context);
        if (set is null)
        {
            return 0;
        }

        // IReadOnlyCollection, not the non-generic ICollection: HashSet<T> does not implement the
        // latter, so casting to it throws InvalidCastException on every call.
        lock (set)
        {
            return ((IReadOnlyCollection<InterceptorSubjectContext>)set).Count;
        }
    }
}
