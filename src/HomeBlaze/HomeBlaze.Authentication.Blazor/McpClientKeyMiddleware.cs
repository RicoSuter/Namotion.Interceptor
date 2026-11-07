using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Http;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Refuses with 401 a request to the external MCP endpoint without the key of the built-in user
/// <see cref="NameRules.McpUserName"/> as a bearer token while that user has a key; passes every request while it has
/// none. Refuses with 403 a request whose <c>Origin</c> header names another scheme, host or port than the request, as
/// only a web page sends one, and MCP clients are no web pages. Refuses every request with 503, not cached, until the
/// <see cref="AuthenticationDatabase"/> is ready, as whether a key exists is not known before. It grants nothing: what a
/// client may do is decided by the policy of that user's roles.
/// </summary>
internal sealed class McpClientKeyMiddleware(RequestDelegate next, IMcpClientAccess access, AuthenticationDatabase database)
{
    private const string BearerPrefix = "Bearer ";

    public Task InvokeAsync(HttpContext context)
    {
        if (!database.IsReady)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }

        if (RequestOrigins.IsCrossOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        if (access.RequiresKey)
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ||
                !access.Verify(authorization[BearerPrefix.Length..].Trim()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Task.CompletedTask;
            }
        }

        return next(context);
    }
}
