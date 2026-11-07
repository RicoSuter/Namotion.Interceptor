using HomeBlaze.Authorization.Abstractions;
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
    private const string PdfContentType = "application/pdf";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Returns the route value of the path as a string without leading slashes, so that <c>//Demo/x.png</c> addresses the
    /// same file as <c>Demo/x.png</c> and does not resolve as an absolute path of its own.
    /// </summary>
    public static string GetSubjectPath(object? routeValue) =>
        (routeValue as string)?.TrimStart('/') ?? string.Empty;

    /// <summary>
    /// Answers the request with the content of <paramref name="subject"/> for a viewer with <paramref name="permissions"/>,
    /// as <see cref="StorageFileEndpointRouteBuilderExtensions.MapStorageFiles"/> describes.
    /// </summary>
    public static async Task HandleAsync(HttpContext context, IInterceptorSubject? subject, IUserPermissions permissions)
    {
        var response = context.Response;

        // A page, a configuration and a file the viewer may not see are answered as missing as well, so that a response
        // does not tell that they exist: a JSON file can hold secrets.
        if (subject is not GenericFile file || !permissions.CanShow(file))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (file.Storage.Status != StorageStatus.Connected)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var contentType = ContentTypes.TryGetContentType(file.Name, out var knownContentType) ? WithCharset(knownContentType) : FallbackContentType;
        // Without a modification time from the storage, a validator would rest on the size alone and keep a replaced file
        // of the same size cached, so the file is then sent in full on every request.
        var entityTag = file.LastModified == default
            ? null
            : new EntityTagHeaderValue($"\"{file.LastModified.Ticks:x}-{file.FileSize:x}\"", isWeak: true);
        if (entityTag is not null && IsNotModified(context.Request, entityTag))
        {
            WriteHeaders(response, file, entityTag, contentType);
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        Stream? content;
        try
        {
            content = await file.ReadAsync(context.RequestAborted);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            content = null;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        // A storage reports an ended connection as an invalid operation; any other cause is a bug that must surface.
        catch (InvalidOperationException) when (file.Storage.Status != StorageStatus.Connected)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // The file was deleted after its path was resolved.
        if (content is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using (content)
        {
            WriteHeaders(response, file, entityTag, contentType);
            response.ContentType = contentType;
            if (content.CanSeek)
            {
                response.ContentLength = content.Length;
            }

            await content.CopyToAsync(response.Body, context.RequestAborted);
        }
    }

    /// <summary>Gets a <c>text/*</c> content type with <c>charset=utf-8</c> when it names no charset, and any other type as given.</summary>
    public static string WithCharset(string contentType) => contentType switch
    {
        // The common types are constants, so that serving them allocates no string.
        "text/plain" => "text/plain; charset=utf-8",
        "text/html" => "text/html; charset=utf-8",
        "text/css" => "text/css; charset=utf-8",
        "text/csv" => "text/csv; charset=utf-8",
        "text/javascript" => "text/javascript; charset=utf-8",
        "text/xml" => "text/xml; charset=utf-8",
        _ when contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Contains("charset=", StringComparison.OrdinalIgnoreCase) => contentType + "; charset=utf-8",
        _ => contentType
    };

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

    private static void WriteHeaders(HttpResponse response, GenericFile file, EntityTagHeaderValue? entityTag, string contentType)
    {
        if (entityTag is not null)
        {
            var headers = response.GetTypedHeaders();
            headers.ETag = entityTag;
            headers.LastModified = new DateTimeOffset(DateTime.SpecifyKind(file.LastModified, DateTimeKind.Utc));
        }

        // Revalidated on every view, so that a file that is replaced shows at once.
        response.Headers.CacheControl = "no-cache";

        // A file opened on its own, such as an SVG with a script, must not run in the origin of the application.
        response.Headers.XContentTypeOptions = "nosniff";

        // A PDF is shown by a plugin-backed viewer that a sandbox blocks, and its scripts cannot reach the origin of the application.
        if (contentType != PdfContentType)
        {
            response.Headers.ContentSecurityPolicy = "sandbox";
        }
    }
}
