using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Services;

/// <summary>
/// The paths that address metadata: of a subject relative to a storage, and of an entry in a metadata file relative
/// to the subject stored beside it. Property names are the segments, separated by slashes, and keys are in brackets
/// (<c>Channels[0]/Power</c>), as in the paths that the registry writes.
/// </summary>
public static class MetadataPaths
{
    /// <summary>Gets the path provider of these paths.</summary>
    public static PathProviderBase PathProvider { get; } = new DefaultPathProvider('/');

    /// <summary>
    /// Resolves a path relative to <paramref name="origin"/> to a subject; the origin itself for a path of only
    /// whitespace and slashes. Leading slashes are skipped, so that a path of the subject tree reaches the subject when
    /// the origin is the root. Null when no subject is at the path or the origin is not registered.
    /// </summary>
    public static IInterceptorSubject? TryResolveSubject(IInterceptorSubject origin, string path)
    {
        var trimmedPath = path.Trim();
        if (trimmedPath.AsSpan().TrimStart('/').IsEmpty)
        {
            return origin;
        }

        return origin.TryGetRegisteredSubject() is { } registered
            ? PathProvider.TryGetSubjectFromPath(registered, trimmedPath)?.Subject
            : null;
    }
}
