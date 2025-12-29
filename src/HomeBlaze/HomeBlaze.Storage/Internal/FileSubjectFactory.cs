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
    private readonly IServiceProvider _subjectServiceProvider;
    private readonly ILogger? _logger;

    public FileSubjectFactory(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        IServiceProvider serviceProvider,
        ILogger? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        // Use wrapper to exclude IInterceptorSubjectContext from DI resolution.
        // Context will be attached later via ContextInheritanceHandler.
        _subjectServiceProvider = new SubjectCreationServiceProvider(serviceProvider);
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
        try
        {
            var subject = _serializer.Deserialize(json);
            if (subject != null)
            {
                // All IConfigurableSubject implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
                return (IInterceptorSubject)subject;
            }
        }
        catch
        {
            // Not a configurable subject, fall through to JsonFile
        }

        // Create JsonFile for plain JSON
        return new JsonFile(storage, blob.FullPath);
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
                _subjectServiceProvider,
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
