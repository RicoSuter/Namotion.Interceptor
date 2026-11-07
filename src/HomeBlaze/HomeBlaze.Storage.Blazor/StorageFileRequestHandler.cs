using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor;

/// <summary>
/// Answers a request for the content of a file of a storage, see
/// <see cref="StorageFileEndpointRouteBuilderExtensions.MapStorageFiles"/>.
/// </summary>
internal static class StorageFileRequestHandler
{
    private const string FallbackContentType = "application/octet-stream";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Writes the content of the subject when it is a <see cref="GenericFile"/>, and 404 for any other subject or none.
    /// </summary>
    public static async Task HandleAsync(HttpContext context, IInterceptorSubject? subject)
    {
        var response = context.Response;

        // A page or a configuration is answered as missing as well, so that a response does not tell that it exists: a
        // JSON file can hold secrets.
        if (subject is not GenericFile file)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (file.Storage.Status != StorageStatus.Connected)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var entityTag = new EntityTagHeaderValue($"\"{file.LastModified.Ticks:x}-{file.FileSize:x}\"", isWeak: true);
        if (IsNotModified(context.Request, entityTag))
        {
            WriteHeaders(response, file, entityTag);
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        Stream? content;
        try
        {
            content = await file.ReadAsync(context.RequestAborted);
        }
        catch (FileNotFoundException)
        {
            content = null;
        }

        // The file was deleted after its path was resolved.
        if (content is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using (content)
        {
            WriteHeaders(response, file, entityTag);
            response.ContentType = ContentTypes.TryGetContentType(file.Name, out var contentType) ? contentType : FallbackContentType;
            if (content.CanSeek)
            {
                response.ContentLength = content.Length;
            }

            await content.CopyToAsync(response.Body, context.RequestAborted);
        }
    }

    private static bool IsNotModified(HttpRequest request, EntityTagHeaderValue entityTag)
    {
        foreach (var candidate in request.GetTypedHeaders().IfNoneMatch)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(entityTag, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteHeaders(HttpResponse response, GenericFile file, EntityTagHeaderValue entityTag)
    {
        var headers = response.GetTypedHeaders();
        headers.ETag = entityTag;
        headers.LastModified = new DateTimeOffset(DateTime.SpecifyKind(file.LastModified, DateTimeKind.Utc));

        // Revalidated on every view, so that a file that is replaced shows at once.
        response.Headers.CacheControl = "no-cache";

        // A file opened on its own, such as an SVG with a script, must not run in the origin of the application.
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "sandbox";
    }
}
