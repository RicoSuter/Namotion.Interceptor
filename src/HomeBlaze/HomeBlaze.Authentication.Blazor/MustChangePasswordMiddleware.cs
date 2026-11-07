using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticAssets;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Refuses with 403 every request of a person with the <see cref="HomeBlazeClaimTypes.MustChangePassword"/> claim,
/// except the pages of the app and its live connection, whose gate shows only the dialog that sets the password,
/// <see cref="AccountEndpoints.PasswordPath"/>, <see cref="AccountEndpoints.LogoutPath"/>, the framework files below
/// <c>/_framework</c> and <c>/_content</c>, and static assets.
/// </summary>
internal sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
    private static readonly PathString FrameworkPath = new("/_framework");
    private static readonly PathString ContentPath = new("/_content");

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.HasClaim(claim => claim.Type == HomeBlazeClaimTypes.MustChangePassword) && !IsExempt(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool IsExempt(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        return endpoint?.Metadata.GetMetadata<AppEndpointMetadata>() is not null ||
            endpoint?.Metadata.GetMetadata<StaticAssetDescriptor>() is not null ||
            IsAccountPath(context.Request.Path) ||
            IsFrameworkPath(context.Request.Path);
    }

    private static bool IsAccountPath(PathString path) =>
        path.Equals(AccountEndpoints.PasswordPath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(AccountEndpoints.LogoutPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsFrameworkPath(PathString path) =>
        path.StartsWithSegments(FrameworkPath, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments(ContentPath, StringComparison.OrdinalIgnoreCase);
}
