using System.Buffers;
using System.Text;
using System.Text.Json;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Provides the read_file tool, which returns the text of Markdown files in storage. Other file types are refused
/// because JSON and text files can hold credentials. Read-only, so it is offered in read-only mode too.
/// </summary>
public class StorageMcpToolProvider : IMcpToolProvider
{
    /// <summary>The maximum number of bytes of file content returned.</summary>
    public const int MaxContentBytes = 64 * 1024;

    private static readonly JsonElement ReadFileSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { path = new { type = "string", description = "Path of a Markdown file subject, e.g. /Docs/home.md" } },
        required = new[] { "path" }
    });

    private static readonly object NotMarkdownError = new
    {
        error = "Not a Markdown file. read_file only reads files whose name ends with .md."
    };

    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly PathProviderBase _pathProvider;
    private readonly ILogger<StorageMcpToolProvider> _logger;

    public StorageMcpToolProvider(
        Func<IInterceptorSubject> rootSubjectProvider,
        PathProviderBase pathProvider,
        ILogger<StorageMcpToolProvider> logger)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _pathProvider = pathProvider;
        _logger = logger;
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = "read_file",
            Description = "Read the text of a Markdown file, such as a page or document, by its path (e.g. /Docs/home.md). " +
                          "Only files whose name ends with .md can be read; any other file type is refused. " +
                          "Content beyond 64 KB is cut off and ends with a [truncated: file has N bytes] marker.",
            InputSchema = ReadFileSchema,
            Handler = HandleReadFileAsync
        };
    }

    private async Task<object?> HandleReadFileAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
        {
            return new { error = "Parameter 'path' is required and must be a string." };
        }

        var subject = ResolveSubject(pathElement.GetString()!);
        if (subject is null)
        {
            return new { error = "Subject not found." };
        }

        // Both names are checked: FullPath is what ReadAsync opens, Name is what the caller sees.
        if (subject.Subject is not IStorageFile file || !IsMarkdown(file.Name) || !IsMarkdown(file.FullPath))
        {
            return NotMarkdownError;
        }

        try
        {
            return await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The exception message is not returned: it is not written for the caller and could quote the file.
            _logger.LogError(exception, "The read_file tool failed to read '{FilePath}'.", file.FullPath);
            return new { error = "Could not read the file. Check server logs for details." };
        }
    }

    private static bool IsMarkdown(string name) =>
        name.EndsWith(FileExtensions.Markdown, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadTextAsync(IStorageFile file, CancellationToken cancellationToken)
    {
        await using var stream = await file.ReadAsync(cancellationToken).ConfigureAwait(false);

        // One byte past the cap tells a file of exactly the cap apart from a longer one without reading further.
        var buffer = ArrayPool<byte>.Shared.Rent(MaxContentBytes + 1);
        try
        {
            var length = await stream.ReadAtLeastAsync(
                buffer.AsMemory(0, MaxContentBytes + 1), MaxContentBytes + 1, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

            var isTruncated = length > MaxContentBytes;
            var end = isTruncated ? GetCharacterBoundary(buffer, MaxContentBytes) : length;
            var start = HasByteOrderMark(buffer, end) ? 3 : 0;
            var text = Encoding.UTF8.GetString(buffer, start, end - start);
            if (!isTruncated)
            {
                return text;
            }

            var totalBytes = stream.CanSeek ? stream.Length : file.FileSize;
            var marker = totalBytes > MaxContentBytes
                ? $"[truncated: file has {totalBytes} bytes]"
                : $"[truncated: file has more than {MaxContentBytes} bytes]";
            return text + "\n\n" + marker;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Moves <paramref name="end"/> back to the start of the UTF-8 sequence it splits, so the cut does not
    /// leave a partial character that would decode as a replacement character.
    /// </summary>
    private static int GetCharacterBoundary(byte[] buffer, int end)
    {
        var boundary = end;
        while (boundary > 0 && end - boundary < 3 && (buffer[boundary] & 0xC0) == 0x80)
        {
            boundary--;
        }

        return (buffer[boundary] & 0xC0) == 0x80 ? end : boundary;
    }

    private static bool HasByteOrderMark(byte[] buffer, int length) =>
        length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF;

    private RegisteredSubject? ResolveSubject(string path)
    {
        var rootRegistered = _rootSubjectProvider().TryGetRegisteredSubject();
        if (rootRegistered is null)
        {
            return null;
        }

        return string.IsNullOrEmpty(path) || path == "/"
            ? rootRegistered
            : _pathProvider.TryGetSubjectFromPath(rootRegistered, path);
    }
}
