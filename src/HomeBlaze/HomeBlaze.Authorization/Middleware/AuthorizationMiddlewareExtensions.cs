using Microsoft.AspNetCore.Builder;

namespace HomeBlaze.Authorization.Middleware;

/// <summary>
/// Extension methods for adding authorization middleware to the request pipeline.
/// </summary>
public static class AuthorizationMiddlewareExtensions
{
    // TODO: Remove? Is this not needed?
    
    /// <summary>
    /// Adds the AuthorizationContext middleware to the request pipeline.
    /// This middleware populates AuthorizationContext from the current HttpContext.User,
    /// enabling authorization checks in interceptors for API requests.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    /// <remarks>
    /// This middleware should be registered after UseAuthentication() and UseAuthorization()
    /// to ensure the user claims are available.
    /// </remarks>
    public static IApplicationBuilder UseAuthorizationContext(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AuthorizationContextMiddleware>();
    }
}