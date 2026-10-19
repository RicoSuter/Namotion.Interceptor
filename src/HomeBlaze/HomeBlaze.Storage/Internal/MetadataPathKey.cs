using System.Text;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The keys of the entries of a metadata file. An entry is written with the path that the registry writes
/// (<c>Channels[0]/Power</c>) and read also in the route notation (<c>Channels/0/Power</c>): the key of a path is its
/// names and keys in order, separated by slashes, so that both notations of a target have the same key.
/// </summary>
internal static class MetadataPathKey
{
    /// <summary>Gets the key of a path, or null when the path is empty or cannot be read.</summary>
    public static string? Normalize(string path)
    {
        if (!MetadataPaths.PathProvider.TryParsePath(path, out var segments, out _) || segments.Count == 0)
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

    /// <summary>Gets the key of a property of the subject with the key <paramref name="subjectKey"/>, which is empty for the stored subject.</summary>
    public static string GetPropertyKey(string subjectKey, string propertyName)
        => subjectKey.Length == 0 ? propertyName : $"{subjectKey}/{propertyName}";

    private static void Append(StringBuilder builder, string token)
    {
        if (builder.Length > 0)
        {
            builder.Append('/');
        }

        builder.Append(token);
    }
}
