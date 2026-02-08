using System;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.WebSocket.Client;

/// <summary>
/// Configuration for WebSocket subject client source.
/// </summary>
public class WebSocketClientConfiguration
{
    /// <summary>
    /// Server URI. Required. Example: "ws://localhost:8080/ws"
    /// </summary>
    public Uri? ServerUri { get; set; }

    /// <summary>
    /// Initial reconnect delay after disconnection. Default: 5 seconds
    /// </summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum reconnect delay (exponential backoff cap). Default: 60 seconds
    /// </summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Connection timeout. Default: 30 seconds
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Receive timeout. If no data is received within this time, the connection is considered lost. Default: 60 seconds
    /// </summary>
    public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Buffer time for batching outbound updates. Default: 8ms
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Retry time for failed writes. Default: 10 seconds
    /// </summary>
    public TimeSpan RetryTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Write retry queue size. Default: 1000
    /// </summary>
    public int WriteRetryQueueSize { get; set; } = 1000;

    /// <summary>
    /// Maximum message size in bytes. Messages larger than this will be rejected. Default: 10MB
    /// </summary>
    public long MaxMessageSize { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum number of property changes per WebSocket message. Default: 1000.
    /// Smaller batches reduce latency, larger batches reduce per-message overhead.
    /// Set to 0 for unlimited (not recommended for large object graphs).
    /// </summary>
    public int WriteBatchSize { get; set; } = 1000;

    /// <summary>
    /// Path provider for property filtering/mapping.
    /// </summary>
    public PathProviderBase? PathProvider { get; set; }

    /// <summary>
    /// Subject factory for creating subjects from server updates.
    /// </summary>
    public ISubjectFactory? SubjectFactory { get; set; }

    /// <summary>
    /// Update processors for filtering/transforming updates.
    /// </summary>
    public ISubjectUpdateProcessor[]? Processors { get; set; }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (ServerUri is null)
        {
            throw new ArgumentException("ServerUri must be specified.", nameof(ServerUri));
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ConnectTimeout must be positive, got: {ConnectTimeout}", nameof(ConnectTimeout));
        }

        if (ReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ReconnectDelay must be positive, got: {ReconnectDelay}", nameof(ReconnectDelay));
        }

        if (MaxReconnectDelay < ReconnectDelay)
        {
            throw new ArgumentException($"MaxReconnectDelay must be >= ReconnectDelay, got: {MaxReconnectDelay}", nameof(MaxReconnectDelay));
        }

        if (WriteRetryQueueSize < 0)
        {
            throw new ArgumentException($"WriteRetryQueueSize must be non-negative, got: {WriteRetryQueueSize}", nameof(WriteRetryQueueSize));
        }

        if (ReceiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ReceiveTimeout must be positive, got: {ReceiveTimeout}", nameof(ReceiveTimeout));
        }

        if (BufferTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"BufferTime must be non-negative, got: {BufferTime}", nameof(BufferTime));
        }

        if (MaxMessageSize <= 0)
        {
            throw new ArgumentException($"MaxMessageSize must be positive, got: {MaxMessageSize}", nameof(MaxMessageSize));
        }

        if (WriteBatchSize < 0)
        {
            throw new ArgumentException($"WriteBatchSize must be non-negative, got: {WriteBatchSize}", nameof(WriteBatchSize));
        }
    }
}
