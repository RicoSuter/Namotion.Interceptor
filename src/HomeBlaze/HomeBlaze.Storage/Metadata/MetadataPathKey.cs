using System.Text;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// The keys of the entries of a metadata file. An entry is written with the path that the registry writes
/// (<c>Channels[0]/Power</c>) and read also in the route notation (<c>Channels/0/Power</c>): the key of a path is its
/// names and keys in order, separated by slashes, so that both notations of a target have the same key. A slash or a
/// backslash in a name or a key is escaped with a backslash, so that a key with a slash differs from more segments.
/// </summary>
internal static class MetadataPathKey
{
    private static readonly DefaultPathProvider PathProvider = new('/');

    /// <summary>Gets the key of a path, or null when the path is empty or cannot be read.</summary>
    public static string? Normalize(string path)
    {
        if (!PathProvider.TryParsePath(path, out var segments, out _) || segments.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(path.Length);
        foreach (var (name, index) in segments)
        {
            Append(builder, name);
            if (index is not null)
            {
                Append(builder, index);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gets the key of a subject inside a stored subject: the key of the path that <see cref="GetPath"/> gives; empty for
    /// the stored subject itself, and null when the subject cannot be reached from it.
    /// </summary>
    public static string? GetKey(RegisteredSubject subject, RegisteredSubject storedSubject)
        => GetPath(subject, storedSubject) is { } path ? (path.Length == 0 ? string.Empty : Normalize(path)) : null;

    /// <summary>
    /// Gets the path that the registry writes for a subject inside a stored subject, through the first parent of each
    /// subject when that way reaches the stored subject, which an entry of the subject is written with; empty for the
    /// stored subject itself, and null when the subject cannot be reached from it.
    /// </summary>
    public static string? GetPath(RegisteredSubject subject, RegisteredSubject storedSubject)
    {
        if (ReferenceEquals(subject, storedSubject))
        {
            return string.Empty;
        }

        // Read once, as a detach can change the parents between two reads.
        var parents = subject.Parents;
        return parents.Length > 0
            ? parents[0].Property.TryGetPath(PathProvider, storedSubject.Subject, parents[0].Index)
            : null;
    }

    /// <summary>Gets the key of a property of the subject with the key <paramref name="subjectKey"/>, which is empty for the stored subject.</summary>
    public static string GetPropertyKey(string subjectKey, string propertyName)
        => subjectKey.Length == 0 ? Escape(propertyName) : $"{subjectKey}/{Escape(propertyName)}";

    private static void Append(StringBuilder builder, string token)
    {
        if (builder.Length > 0)
        {
            builder.Append('/');
        }

        builder.Append(Escape(token));
    }

    private static string Escape(string token)
        => token.Replace("\\", "\\\\").Replace("/", "\\/");
}
