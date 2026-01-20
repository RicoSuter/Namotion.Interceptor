using System;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Configuration for WebSocket subject server.
/// </summary>
public class WebSocketServerConfiguration
{
    /// <summary>
    /// Port to listen on. Default: 8080
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// WebSocket path. Default: "/ws"
    /// </summary>
    public string Path { get; set; } = "/ws";

    /// <summary>
    /// Bind address. Default: any (null)
    /// </summary>
    public string? BindAddress { get; set; }

    /// <summary>
    /// Buffer time for batching outbound updates. Default: 8ms
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Maximum number of property changes per WebSocket message. Default: 1000.
    /// Smaller batches reduce latency, larger batches reduce per-message overhead.
    /// Set to 0 for unlimited (not recommended for large object graphs).
    /// </summary>
    public int WriteBatchSize { get; set; } = 1000;

    /// <summary>
    /// Maximum message size in bytes. Messages larger than this will be rejected. Default: 10MB
    /// </summary>
    public long MaxMessageSize { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum number of concurrent connections. Default: 1000
    /// </summary>
    public int MaxConnections { get; set; } = 1000;

    /// <summary>
    /// Path provider for property filtering/mapping.
    /// </summary>
    public PathProviderBase? PathProvider { get; set; }

    /// <summary>
    /// Subject factory for creating subjects from client updates.
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
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Port must be between 1 and 65535, got: {Port}", nameof(Port));
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException("Path must be specified.", nameof(Path));
        }

        if (BufferTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"BufferTime must be non-negative, got: {BufferTime}", nameof(BufferTime));
        }
    }
}
