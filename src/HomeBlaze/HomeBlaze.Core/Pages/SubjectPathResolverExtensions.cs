using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Core.Pages;

public static class SubjectPathResolverExtensions
{
    /// <summary>
    /// Registers the SubjectPathResolver service which provides path resolution and building capabilities.
    /// Requires WithRegistry() and WithLifecycle() to be called first.
    /// </summary>
    public static IInterceptorSubjectContext WithPathResolver(this IInterceptorSubjectContext context)
    {
        return context
            .WithLifecycle()
            .WithService(() => new SubjectPathResolver());
    }
}
