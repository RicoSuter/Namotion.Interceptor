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
    /// Creates the subject of a file from the type that is registered for its extension. A JSON file is
    /// created from its text with <see cref="CreateFromJson"/> instead.
    /// </summary>
    public async Task<IInterceptorSubject?> CreateFromBlobAsync(
        IStorageContainer storage,
        Blob blob,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(blob.FullPath).ToLowerInvariant();

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

    /// <summary>
    /// Creates the subject of a JSON file from its text: the configurable subject it describes,
    /// or a <see cref="JsonFile"/> when the text is plain or invalid JSON.
    /// </summary>
    public IInterceptorSubject CreateFromJson(IStorageContainer storage, string path, string json)
    {
        try
        {
            var subject = _serializer.Deserialize(json);
            if (subject != null)
            {
                // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
                return (IInterceptorSubject)subject;
            }
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to deserialize JSON subject from: {Path}", path);
        }

        return new JsonFile(storage, path);
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
