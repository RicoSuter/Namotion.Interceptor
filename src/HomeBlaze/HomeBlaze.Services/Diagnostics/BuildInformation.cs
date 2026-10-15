using System.Globalization;
using System.Reflection;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Version, commit, repository and CI metadata of one assembly.
/// </summary>
internal sealed record BuildInformation(
    string Version,
    string? Commit,
    string? RepositoryUrl,
    string? Tag,
    string? Branch,
    DateTimeOffset? CommitDate,
    DateTimeOffset? BuildTime,
    string? RunUrl,
    string? ImageTags)
{
    private const string MetadataPrefix = "Namotion.Build.";

    public static BuildInformation FromAssembly(Assembly assembly)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            metadata[attribute.Key] = attribute.Value;
        }

        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;
        return Create(informationalVersion, metadata);
    }

    public static BuildInformation Create(string informationalVersion, IReadOnlyDictionary<string, string?> metadata)
    {
        var (version, commit) = ParseInformationalVersion(informationalVersion);
        return new BuildInformation(
            version,
            commit,
            NormalizeRepositoryUrl(GetValue(metadata, "RepositoryUrl")),
            GetValue(metadata, MetadataPrefix + "Tag"),
            GetValue(metadata, MetadataPrefix + "Branch"),
            ParseDate(GetValue(metadata, MetadataPrefix + "CommitDate")),
            ParseDate(GetValue(metadata, MetadataPrefix + "Time")),
            GetValue(metadata, MetadataPrefix + "RunUrl"),
            GetValue(metadata, MetadataPrefix + "ImageTags"));
    }

    /// <summary>
    /// Splits an informational version such as <c>1.2.3+abc1234</c> into the version and the commit. The commit is the
    /// text after the <c>+</c> up to the first <c>.</c>, and only when it consists of 7 to 40 hexadecimal digits.
    /// </summary>
    public static (string Version, string? Commit) ParseInformationalVersion(string informationalVersion)
    {
        var plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex < 0)
        {
            return (informationalVersion, null);
        }

        var buildMetadata = informationalVersion.AsSpan(plusIndex + 1);
        var dotIndex = buildMetadata.IndexOf('.');
        var commit = dotIndex < 0 ? buildMetadata : buildMetadata[..dotIndex];
        return (informationalVersion[..plusIndex], IsCommit(commit) ? commit.ToString().ToLowerInvariant() : null);
    }

    /// <summary>
    /// Removes surrounding whitespace, trailing slashes and a <c>.git</c> suffix, or returns null for an empty URL.
    /// </summary>
    public static string? NormalizeRepositoryUrl(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return null;
        }

        var normalized = repositoryUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4].TrimEnd('/');
        }

        return normalized.Length > 0 ? normalized : null;
    }

    private static bool IsCommit(ReadOnlySpan<char> value)
    {
        if (value.Length is < 7 or > 40)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string?> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
}
