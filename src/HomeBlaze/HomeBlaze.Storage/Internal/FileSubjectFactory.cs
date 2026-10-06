using System.Reflection;
using System.Text.Json;
using FluentStorage.Blobs;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Factory for creating IInterceptorSubject instances from storage blobs
/// and updating existing subjects from JSON.
/// </summary>
internal sealed class FileSubjectFactory
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger? _logger;

    public FileSubjectFactory(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        IServiceProvider serviceProvider,
        ILogger? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates a subject from a storage blob based on file type.
    /// </summary>
    public async Task<IInterceptorSubject?> CreateFromBlobAsync(
        IBlobStorage client,
        IStorageContainer storage,
        Blob blob,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(blob.FullPath).ToLowerInvariant();
        if (extension == FileExtensions.Json)
        {
            return await CreateFromJsonBlobAsync(client, storage, blob, cancellationToken);
        }

        // Check for registered extension mapping
        var mappedType = _typeRegistry.ResolveTypeForExtension(extension);
        if (mappedType != null)
        {
            var subject = CreateFileSubject(mappedType, storage, blob.FullPath);
            UpdateFileMetadata(subject, blob);

            // Eager load content for storage files
            if (subject is IStorageFile storageFile)
            {
                await storageFile.OnFileChangedAsync(cancellationToken);
            }

            return subject;
        }

        // Default to GenericFile
        var genericFile = new GenericFile(storage, blob.FullPath);
        UpdateFileMetadata(genericFile, blob);
        await genericFile.OnFileChangedAsync(cancellationToken);
        return genericFile;
    }

    /// <summary>
    /// Serializes a subject to JSON.
    /// </summary>
    public string Serialize(IInterceptorSubject subject)
        => _serializer.Serialize(subject);

    private async Task<IInterceptorSubject?> CreateFromJsonBlobAsync(
        IBlobStorage client,
        IStorageContainer storage,
        Blob blob,
        CancellationToken cancellationToken)
    {
        var json = await client.ReadTextAsync(blob.FullPath, cancellationToken: cancellationToken);

        string? typeName;
        try
        {
            typeName = TryReadTypeName(json);
        }
        catch (JsonException exception)
        {
            // A file with a $type marker is a device file whose content is corrupt, not plain data:
            // it stays visible as a placeholder so the scan does not silently misclassify it as a JsonFile.
            if (!json.Contains("\"$type\"", StringComparison.Ordinal))
            {
                return new JsonFile(storage, blob.FullPath);
            }

            _logger?.LogWarning(exception, "Invalid JSON with a $type marker in: {Path}", blob.FullPath);
            var invalidJsonSubject = new UnknownSubject(storage, blob.FullPath, string.Empty, $"Invalid JSON: {exception.Message}");
            UpdateFileMetadata(invalidJsonSubject, blob);
            return invalidJsonSubject;
        }

        if (typeName is null)
        {
            return new JsonFile(storage, blob.FullPath);
        }

        string reason;
        try
        {
            var subject = _serializer.Deserialize(json);
            if (subject != null)
            {
                // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
                return (IInterceptorSubject)subject;
            }

            reason = UnknownSubject.TypeNotLoadedReason;
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to create subject of type {Type} from: {Path}", typeName, blob.FullPath);

            // ActivatorUtilities invokes the constructor directly for a type with no DI parameters, so the
            // original exception usually isn't wrapped; TargetInvocationException only shows up for overloads
            // it reaches through reflection. Either way, the outer message is the one the constructor chose
            // to surface, so prefer it over an inner cause's message.
            reason = exception is TargetInvocationException { InnerException: { } inner } ? inner.Message : exception.Message;
        }

        var unknownSubject = new UnknownSubject(storage, blob.FullPath, typeName, reason);
        UpdateFileMetadata(unknownSubject, blob);
        return unknownSubject;
    }

    private static string? TryReadTypeName(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("$type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var typeName = typeElement.GetString();
        return string.IsNullOrWhiteSpace(typeName) ? null : typeName;
    }

    /// <summary>
    /// Creates a file subject using ActivatorUtilities for DI-aware construction.
    /// Convention: File constructors should be (IStorageContainer storage, string fullPath, /* DI services... */)
    /// </summary>
    private IInterceptorSubject? CreateFileSubject(Type type, IStorageContainer storage, string blobPath)
    {
        try
        {
            // ActivatorUtilities resolves DI services + passes explicit args
            return (IInterceptorSubject)ActivatorUtilities.CreateInstance(
                _serviceProvider,
                type,
                storage,   // explicit arg
                blobPath); // explicit arg
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create file subject for: {Path}", blobPath);
        }

        return null;
    }

    private static void UpdateFileMetadata(IInterceptorSubject? subject, Blob blob)
    {
        if (subject is IStorageFile file)
        {
            file.FileSize = blob.Size ?? 0L;
            if (blob.LastModificationTime.HasValue)
            {
                file.LastModified = blob.LastModificationTime.Value.DateTime;
            }
        }
    }
}
