using HomeBlaze.Authentication.Blazor.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Answers every request with the 503 page <see cref="Unavailable"/>, not cached, saying that authentication is
/// unavailable, and why, until the <see cref="AuthenticationDatabase"/> is ready, so that nothing opens up while it
/// starts or cannot be used; the health endpoints and static assets still answer.
/// </summary>
internal sealed class AuthenticationUnavailableMiddleware(RequestDelegate next, AuthenticationDatabase database)
{
    // The endpoints of MapDefaultEndpoints.
    private static readonly PathString HealthPath = new("/health");
    private static readonly PathString AlivePath = new("/alive");

    private const string StartingReason = "HomeBlaze is starting.";

    public async Task InvokeAsync(HttpContext context)
    {
        if (database.IsReady || IsExempt(context))
        {
            await next(context);
            return;
        }

        var html = await RenderAsync(context.RequestServices, database.UnavailableReason ?? StartingReason);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static async Task<string> RenderAsync(IServiceProvider services, string reason)
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Unavailable.Reason)] = reason });
            return (await renderer.RenderComponentAsync<Unavailable>(parameters)).ToHtmlString();
        });
    }

    private static bool IsExempt(HttpContext context) =>
        context.Request.Path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals(AlivePath, StringComparison.OrdinalIgnoreCase) ||
        context.GetEndpoint()?.Metadata.GetMetadata<StaticAssetDescriptor>() is not null;
}
