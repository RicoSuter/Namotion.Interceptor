namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Represents a downloaded NuGet package with its metadata and content stream.
/// Disposing this object disposes the underlying stream.
/// </summary>
public class NuGetPackageDownload : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPackageDownload"/> class.
    /// </summary>
    public NuGetPackageDownload(NuGetPackage package, Stream stream)
    {
        Package = package;
        Stream = stream;
    }

    /// <summary>
    /// Gets the package metadata.
    /// </summary>
    public NuGetPackage Package { get; }

    /// <summary>
    /// Gets the package content stream (.nupkg).
    /// </summary>
    public Stream Stream { get; }

    /// <inheritdoc />
    public void Dispose() => Stream.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
