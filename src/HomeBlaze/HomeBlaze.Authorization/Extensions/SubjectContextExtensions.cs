using HomeBlaze.Authorization.Interceptors;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

namespace HomeBlaze.Authorization.Extensions;

/// <summary>
/// Extension methods for adding authorization to InterceptorSubjectContext.
/// </summary>
public static class SubjectContextExtensions
{
    /// <summary>
    /// Adds the AuthorizationInterceptor to the context for property read/write authorization checks.
    /// </summary>
    /// <param name="context">The subject context.</param>
    /// <returns>The context for fluent chaining.</returns>
    public static IInterceptorSubjectContext WithAuthorization(this IInterceptorSubjectContext context)
    {
        var interceptor = new AuthorizationInterceptor();

        // Add as both read and write interceptor (it implements both)
        context.TryAddService<IReadInterceptor>(() => interceptor, i => i is AuthorizationInterceptor);
        context.TryAddService<IWriteInterceptor>(() => interceptor, i => i is AuthorizationInterceptor);

        return context;
    }
}
