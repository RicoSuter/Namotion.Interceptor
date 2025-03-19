using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Adds the registry which tracks and extends subjects.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithRegistry(this IInterceptorSubjectContext context)
    {
        context
            .TryAddService(() => new SubjectRegistry(), _ => true);

        return context
            .WithContextInheritance();
    }
}