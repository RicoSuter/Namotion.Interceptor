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
    internal static string GetSubjectPath(object? routeValue) =>
        (routeValue as string)?.TrimStart('/') ?? string.Empty;

    /// <summary>
    /// Writes the content of the subject when it is a <see cref="GenericFile"/>, and 404 for any other subject or none or a
    /// file that is gone, and 503 when the storage is not connected or cannot deliver the content now.
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

        var contentType = ContentTypes.TryGetContentType(file.Name, out var knownContentType) ? knownContentType : FallbackContentType;
        var entityTag = new EntityTagHeaderValue($"\"{file.LastModified.Ticks:x}-{file.FileSize:x}\"", isWeak: true);
        if (IsNotModified(context.Request, entityTag))
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

    private static void WriteHeaders(HttpResponse response, GenericFile file, EntityTagHeaderValue entityTag, string contentType)
    {
        var headers = response.GetTypedHeaders();
        headers.ETag = entityTag;
        headers.LastModified = new DateTimeOffset(DateTime.SpecifyKind(file.LastModified, DateTimeKind.Utc));

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
