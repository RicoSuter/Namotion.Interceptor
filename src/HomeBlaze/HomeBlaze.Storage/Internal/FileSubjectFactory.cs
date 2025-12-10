using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
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
    private readonly ILogger? _logger;

    public FileSubjectFactory(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        ILogger? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
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
        if (extension == ".json")
        {
            return await CreateFromJsonBlobAsync(client, storage, blob, cancellationToken);
        }

        // Check for registered extension mapping
        var mappedType = _typeRegistry.ResolveTypeForExtension(extension);
        if (mappedType != null)
        {
            var subject = CreateFileSubject(mappedType, storage, blob.FullPath);
            SetFileMetadata(subject, blob);

            if (subject is MarkdownFile markdownFile)
            {
                await LoadMarkdownTitleAsync(client, markdownFile, blob, cancellationToken);
            }

            return subject;
        }

        // Default to GenericFile
        var genericFile = new GenericFile(storage, blob.FullPath);
        SetFileMetadata(genericFile, blob);
        return genericFile;
    }

    /// <summary>
    /// Updates an existing subject's configuration properties from JSON and calls ApplyConfigurationAsync.
    /// </summary>
    public async Task UpdateFromJsonAsync(
        IInterceptorSubject subject, string json, CancellationToken cancellationToken)
    {
        _serializer.UpdateConfiguration(subject, json);

        if (subject is IConfigurableSubject configurable)
        {
            await configurable.ApplyConfigurationAsync(cancellationToken);
        }
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
                return subject;
            }
        }
        catch
        {
            // Not a configurable subject, fall through to JsonFile
        }

        // Create JsonFile for plain JSON
        return new JsonFile(storage, blob.FullPath);
    }

    private async Task LoadMarkdownTitleAsync(
        IBlobStorage client,
        MarkdownFile markdownFile,
        Blob blob,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await client.ReadTextAsync(blob.FullPath, cancellationToken: cancellationToken);
            markdownFile.Content = content;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load markdown content for: {Path}", blob.FullPath);
        }
    }

    private IInterceptorSubject? CreateFileSubject(Type type, IStorageContainer storage, string blobPath)
    {
        try
        {
            // Try constructor with (IStorageContainer, string)
            var ctor = type.GetConstructor([typeof(IStorageContainer), typeof(string)]);
            if (ctor != null)
            {
                return (IInterceptorSubject)ctor.Invoke([storage, blobPath]);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create file subject for: {Path}", blobPath);
        }

        return null;
    }

    private static void SetFileMetadata(IInterceptorSubject? subject, Blob blob)
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
