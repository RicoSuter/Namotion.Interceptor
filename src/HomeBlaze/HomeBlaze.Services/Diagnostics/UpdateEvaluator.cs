using NuGet.Versioning;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The newest published release, with the version without its <c>v</c> prefix.
/// </summary>
internal sealed record LatestRelease(string Version, DateTimeOffset? PublishedAt, Uri? Url);

internal static class UpdateEvaluator
{
    /// <summary>
    /// Returns whether <paramref name="release"/> is newer than the running release: false when there is no published
    /// release, and null when the running version does not parse as SemVer.
    /// </summary>
    public static bool? IsUpdateAvailable(string runningVersion, LatestRelease? release)
    {
        if (release is null)
        {
            return false;
        }

        return NuGetVersion.TryParse(runningVersion, out _) ? IsNewer(release.Version, runningVersion) : null;
    }

    /// <summary>
    /// Returns whether <paramref name="candidate"/> is a greater semantic version than <paramref name="current"/>,
    /// ignoring build metadata. Versions that do not parse are never newer.
    /// </summary>
    public static bool IsNewer(string candidate, string current) =>
        NuGetVersion.TryParse(candidate, out var candidateVersion) &&
        NuGetVersion.TryParse(current, out var currentVersion) &&
        VersionComparer.VersionRelease.Compare(candidateVersion, currentVersion) > 0;
}
