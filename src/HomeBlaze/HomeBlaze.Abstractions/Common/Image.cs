namespace HomeBlaze.Abstractions.Common;

/// <summary>
/// Represents an immutable image with its binary data and content type.
/// </summary>
public sealed record Image
{
    /// <summary>
    /// The raw image data.
    /// </summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>
    /// The MIME content type (e.g., "image/jpeg", "image/png").
    /// </summary>
    public required string ContentType { get; init; }
}
