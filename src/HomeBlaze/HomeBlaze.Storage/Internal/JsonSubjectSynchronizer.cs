using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Handles synchronization of JSON-backed IConfigurableSubject instances with storage.
/// Manages hash/size comparison, retry logic, and deserialization.
/// </summary>
internal sealed class JsonSubjectSynchronizer
{
    private readonly StoragePathRegistry _pathRegistry;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly IBlobStorage _client;
    private readonly ILogger? _logger;

    public JsonSubjectSynchronizer(
        StoragePathRegistry pathRegistry,
        ConfigurableSubjectSerializer serializer,
        IBlobStorage client,
        ILogger? logger = null)
    {
        _pathRegistry = pathRegistry;
        _serializer = serializer;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to refresh an IConfigurableSubject from storage if the content has changed.
    /// </summary>
    /// <returns>True if refresh was attempted (even if skipped due to no change), false if subject is not IConfigurableSubject.</returns>
    public async Task<bool> TryRefreshAsync(
        IInterceptorSubject subject,
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (subject is not IConfigurableSubject)
            return false;

        // Size check
        long newSize;
        try
        {
            var fileInfo = new FileInfo(fullPath);
            newSize = fileInfo.Length;
        }
        catch
        {
            return false;
        }

        var sizeChanged = _pathRegistry.HasSizeChanged(relativePath, newSize);

        // Hash check with retry
        string? newHash = null;
        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                await using var stream = await _client.OpenReadAsync(relativePath, cancellationToken: cancellationToken);
                newHash = await StoragePathRegistry.ComputeHashAsync(stream);
                break;
            }
            catch (IOException) when (retry < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, retry)), cancellationToken);
            }
        }

        if (newHash == null)
        {
            _logger?.LogWarning("Could not compute hash after retries: {Path}", relativePath);
            return false;
        }

        if (!sizeChanged && !_pathRegistry.HasHashChanged(relativePath, newHash))
        {
            _logger?.LogDebug("File unchanged (same hash), skipping reload: {Path}", relativePath);
            return true;
        }

        _pathRegistry.UpdateSize(relativePath, newSize);
        _pathRegistry.UpdateHash(relativePath, newHash);

        // Deserialize and update
        try
        {
            var json = await _client.ReadTextAsync(relativePath, cancellationToken: cancellationToken);
            _serializer.UpdateConfiguration(subject, json);

            if (subject is IConfigurableSubject configurable)
            {
                await configurable.ApplyConfigurationAsync(cancellationToken);
            }

            _logger?.LogInformation("Reloaded JSON subject: {Path}", relativePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to update from JSON: {Path}", relativePath);
        }

        return true;
    }
}
