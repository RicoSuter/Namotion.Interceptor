using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Builder;
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
    /// route path, as the relative links and pictures of a markdown page address it. Only a file that is no page, no JSON
    /// file and no subject of its own type is served (a <see cref="HomeBlaze.Storage.Files.GenericFile"/>). Every other
    /// path is answered with 404.
    /// </summary>
    /// <returns>The builder of the endpoint, for example to require authorization.</returns>
    public static IEndpointConventionBuilder MapStorageFiles(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(StorageFileRoute.Prefix + "/{**path}", static context =>
        {
            var path = context.Request.RouteValues["path"] as string ?? string.Empty;
            var subject = context.RequestServices.GetRequiredService<SubjectPathResolver>().ResolveSubject(path, PathStyle.Route);
            return StorageFileRequestHandler.HandleAsync(context, subject);
        });
}
