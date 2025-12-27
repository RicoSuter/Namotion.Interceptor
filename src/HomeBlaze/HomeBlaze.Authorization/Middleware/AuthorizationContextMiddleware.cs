using HomeBlaze.Authorization.Context;
using HomeBlaze.Authorization.Services;
using Microsoft.AspNetCore.Http;

namespace HomeBlaze.Authorization.Middleware;

/// <summary>
/// Middleware that populates AuthorizationContext from the current HttpContext.User.
/// This enables authorization checks in interceptors for API requests.
/// </summary>
public class AuthorizationContextMiddleware
{
    private readonly RequestDelegate _next;

    public AuthorizationContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IRoleExpander roleExpander)
    {
        try
        {
            AuthorizationContext.PopulateFromUser(context.User, roleExpander.ExpandRoles);
            await _next(context);
        }
        finally
        {
            AuthorizationContext.Clear();
        }
    }
}