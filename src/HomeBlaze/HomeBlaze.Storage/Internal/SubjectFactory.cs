using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Core;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Factory for creating IInterceptorSubject instances from storage blobs
/// and updating existing subjects from JSON.
/// </summary>
internal sealed class SubjectFactory
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly ILogger? _logger;

    public SubjectFactory(
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
        IInterceptorSubjectContext context,
        CancellationToken ct)
    {
        var extension = Path.GetExtension(blob.FullPath).ToLowerInvariant();

        // JSON files - check if configurable type
        if (extension == ".json")
        {
            return await CreateFromJsonBlobAsync(client, storage, blob, context, ct);
        }

        // Check for registered extension mapping
        var mappedType = _typeRegistry.ResolveTypeForExtension(extension);
        if (mappedType != null)
        {
            var subject = CreateFileSubject(mappedType, storage, blob.FullPath);
            SetFileMetadata(subject, blob);

            if (subject is MarkdownFile markdownFile)
            {
                await LoadMarkdownTitleAsync(client, markdownFile, blob, ct);
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
        IInterceptorSubject subject,
        string json,
        CancellationToken ct = default)
    {
        _serializer.UpdateConfiguration(subject, json);

        if (subject is IConfigurableSubject configurable)
        {
            await configurable.ApplyConfigurationAsync(ct);
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
        IInterceptorSubjectContext context,
        CancellationToken ct)
    {
        var json = await client.ReadTextAsync(blob.FullPath, cancellationToken: ct);

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
        CancellationToken ct)
    {
        try
        {
            var content = await client.ReadTextAsync(blob.FullPath, cancellationToken: ct);
            var title = MarkdownFile.ExtractTitleFromContent(content);
            if (title != null)
            {
                markdownFile.SetTitle(title);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load markdown title for: {Path}", blob.FullPath);
        }
    }

    private IInterceptorSubject? CreateFileSubject(
        Type type,
        IStorageContainer storage,
        string blobPath)
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
        if (subject == null)
            return;

        if (subject is GenericFile genericFile)
        {
            genericFile.FileSize = blob.Size ?? 0L;
            if (blob.LastModificationTime.HasValue)
                genericFile.LastModified = blob.LastModificationTime.Value.DateTime;
        }
        else if (subject is MarkdownFile markdownFile)
        {
            markdownFile.FileSize = blob.Size ?? 0L;
            if (blob.LastModificationTime.HasValue)
                markdownFile.LastModified = blob.LastModificationTime.Value.DateTime;
        }
        else if (subject is JsonFile jsonFile)
        {
            jsonFile.FileSize = blob.Size ?? 0L;
            if (blob.LastModificationTime.HasValue)
                jsonFile.LastModified = blob.LastModificationTime.Value.DateTime;
        }
    }
}
