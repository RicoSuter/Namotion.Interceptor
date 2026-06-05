namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Represents a downloaded NuGet package with its metadata and content stream.
/// Disposing this object disposes the underlying stream and any associated resource owner.
/// </summary>
public class NuGetPackageDownload : IAsyncDisposable, IDisposable
{
    private readonly IDisposable? _resourceOwner;

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPackageDownload"/> class.
    /// </summary>
    public NuGetPackageDownload(NuGetPackage package, Stream stream)
        : this(package, stream, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPackageDownload"/> class
    /// with an optional resource owner that will be disposed alongside the stream.
    /// </summary>
    internal NuGetPackageDownload(NuGetPackage package, Stream stream, IDisposable? resourceOwner)
    {
        Package = package;
        Stream = stream;
        _resourceOwner = resourceOwner;
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
    public void Dispose()
    {
        Stream.Dispose();
        _resourceOwner?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _resourceOwner?.Dispose();
    }
}
