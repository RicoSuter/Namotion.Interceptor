using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Storage.Blazor;

/// <summary>
/// Maps the endpoint that serves the content of the files of the storages.
/// </summary>
public static class StorageFileEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET /files/{path}</c> (<see cref="StorageFileRoute.Prefix"/>), which serves the content of the file at that
    /// route path. Only a file that is no page, no JSON file and no subject of its own type is served (a
    /// <see cref="HomeBlaze.Storage.Files.GenericFile"/>), and only when the <see cref="IUserPermissions"/> of the request
    /// show it (<see cref="IUserPermissions.CanShow"/>), as the pages do. Every other path, and a file that is not shown, is
    /// answered with 404, and 503 when the storage cannot deliver the file now. Every response of a file has <c>X-Content-Type-Options: nosniff</c> and, except for a
    /// PDF, <c>Content-Security-Policy: sandbox</c>. A <c>text/*</c> content type is served with <c>charset=utf-8</c>.
    /// </summary>
    /// <returns>The builder of the endpoint, for example to require authorization.</returns>
    public static IEndpointConventionBuilder MapStorageFiles(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(StorageFileRoute.Prefix + "/{**path}", static context =>
        {
            var path = StorageFileRequestHandler.GetSubjectPath(context.Request.RouteValues["path"]);
            var subject = context.RequestServices.GetRequiredService<SubjectPathResolver>().ResolveSubject(path, PathStyle.Route);
            return StorageFileRequestHandler.HandleAsync(context, subject, GetPermissions(context));
        });

    // Outside a circuit nothing sets the authentication state of the request, which the permissions are computed from,
    // so it is set from the user of the request, as for a page that the server renders.
    private static IUserPermissions GetPermissions(HttpContext context)
    {
        var services = context.RequestServices;
        if (services.GetService<AuthenticationStateProvider>() is IHostEnvironmentAuthenticationStateProvider authenticationState)
        {
            authenticationState.SetAuthenticationState(Task.FromResult(new AuthenticationState(context.User)));
        }

        return services.GetRequiredService<IUserPermissions>();
    }
}
