using System.Text.Json;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Core.Extensions;
using HomeBlaze.Core.Services;
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
    private readonly SubjectSerializer _serializer;
    private readonly ILogger? _logger;

    public SubjectFactory(
        SubjectTypeRegistry typeRegistry,
        SubjectSerializer serializer,
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
        FluentStorageContainer storage,
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
            var subject = CreateFileSubject(mappedType, storage, blob.FullPath, context);
            SetFileMetadata(subject, blob);

            if (subject is MarkdownFile markdownFile)
            {
                await LoadMarkdownTitleAsync(client, markdownFile, blob, ct);
            }

            return subject;
        }

        // Default to GenericFile
        var genericFile = CreateFileSubject(typeof(GenericFile), storage, blob.FullPath, context);
        SetFileMetadata(genericFile, blob);
        return genericFile;
    }

    /// <summary>
    /// Updates an existing subject's configuration properties from JSON and calls ReloadAsync.
    /// </summary>
    public async Task UpdateFromJsonAsync(
        IInterceptorSubject subject,
        string json,
        CancellationToken ct = default)
    {
        // Update configuration properties from JSON
        UpdatePropertiesFromJson(subject, json);

        // Notify subject that properties have been updated
        if (subject is IPersistentSubject persistent)
        {
            await persistent.ReloadAsync(ct);
        }
    }

    /// <summary>
    /// Serializes a subject to JSON.
    /// </summary>
    public string Serialize(IInterceptorSubject subject)
        => _serializer.Serialize(subject);

    private async Task<IInterceptorSubject?> CreateFromJsonBlobAsync(
        IBlobStorage client,
        FluentStorageContainer storage,
        Blob blob,
        IInterceptorSubjectContext context,
        CancellationToken ct)
    {
        var json = await client.ReadTextAsync(blob.FullPath, cancellationToken: ct);

        // Try to deserialize as a configurable subject
        try
        {
            var subject = _serializer.Deserialize(json, context);
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
        return new JsonFile(context, storage, blob.FullPath) { Content = json };
    }

    private void UpdatePropertiesFromJson(IInterceptorSubject subject, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var prop in subject.GetConfigurationProperties())
            {
                var jsonName = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
                if (root.TryGetProperty(jsonName, out var jsonValue))
                {
                    try
                    {
                        var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), prop.Type);
                        prop.SetValue(value);
                    }
                    catch
                    {
                        // Skip properties that can't be deserialized
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse JSON for property update");
        }
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
        FluentStorageContainer storage,
        string blobPath,
        IInterceptorSubjectContext context)
    {
        try
        {
            // Try constructor with (context, storage, path)
            var ctor = type.GetConstructor([typeof(IInterceptorSubjectContext), typeof(FluentStorageContainer), typeof(string)]);
            if (ctor != null)
            {
                return (IInterceptorSubject)ctor.Invoke([context, storage, blobPath]);
            }

            // Try constructor with (storage, path)
            ctor = type.GetConstructor([typeof(FluentStorageContainer), typeof(string)]);
            if (ctor != null)
            {
                return (IInterceptorSubject)ctor.Invoke([storage, blobPath]);
            }

            // Try constructor with just (path)
            ctor = type.GetConstructor([typeof(string)]);
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke([blobPath]);
                ReflectionHelper.SetPropertyIfExists(subject, "Storage", storage);
                return subject;
            }

            // Try constructor with just context
            ctor = type.GetConstructor([typeof(IInterceptorSubjectContext)]);
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke([context]);
                ReflectionHelper.SetPropertyIfExists(subject, "Storage", storage);
                ReflectionHelper.SetPropertyIfExists(subject, "BlobPath", blobPath);
                ReflectionHelper.SetPropertyIfExists(subject, "FilePath", blobPath);
                ReflectionHelper.SetPropertyIfExists(subject, "FileName", Path.GetFileName(blobPath));
                return subject;
            }

            // Try parameterless constructor
            ctor = type.GetConstructor([]);
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke([]);
                ReflectionHelper.SetPropertyIfExists(subject, "Storage", storage);
                ReflectionHelper.SetPropertyIfExists(subject, "BlobPath", blobPath);
                ReflectionHelper.SetPropertyIfExists(subject, "FilePath", blobPath);
                ReflectionHelper.SetPropertyIfExists(subject, "FileName", Path.GetFileName(blobPath));
                return subject;
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

        ReflectionHelper.SetPropertyIfExists(subject, "FileSize", blob.Size ?? 0L);
        if (blob.LastModificationTime.HasValue)
        {
            ReflectionHelper.SetPropertyIfExists(subject, "LastModified", blob.LastModificationTime.Value);
        }
    }
}
