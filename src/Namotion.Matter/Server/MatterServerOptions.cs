namespace Namotion.Matter.Server;

/// <summary>Options of a <see cref="MatterServerController"/>.</summary>
public sealed record MatterServerOptions
{
    /// <summary>
    /// Gets how long a request other than commissioning waits for its answer, and how long connecting waits for the
    /// server's information. The default is 60 seconds, above the server's own response timeout for a device.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the largest message accepted from the server, in bytes. The default is 64 MB.</summary>
    public int MaxMessageSize { get; init; } = 64 * 1024 * 1024;
}
