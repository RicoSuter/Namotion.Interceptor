using Microsoft.AspNetCore.Http;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Refuses with 403 a request to the Blazor hub whose <c>Origin</c> header names another scheme, host or port than the
/// request, because the session cookie is <c>SameSite=Lax</c>, which ignores ports, so other services on the same host
/// would count as the same site. A request without an <c>Origin</c> header passes.
/// </summary>
internal sealed class BlazorOriginCheckMiddleware(RequestDelegate next)
{
    private static readonly PathString HubPath = new("/_blazor");

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(HubPath) && RequestOrigins.IsCrossOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
