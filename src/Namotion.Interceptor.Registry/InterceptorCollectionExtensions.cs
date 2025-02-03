using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

public static class InterceptorCollectionExtensions
{
    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
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