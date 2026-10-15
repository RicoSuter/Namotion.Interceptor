using NuGet.Versioning;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The newest published release, with the version without its <c>v</c> prefix.
/// </summary>
internal sealed record LatestRelease(string Version, DateTimeOffset? PublishedAt, Uri? Url);

/// <summary>
/// How many commits a branch is ahead of the running commit.
/// </summary>
internal sealed record BranchComparison(int CommitsBehind, Uri? Url);

internal sealed record UpdateState(
    bool? UpdateAvailable,
    string? LatestVersion,
    DateTimeOffset? LatestReleaseDate,
    Uri? LatestReleaseUrl,
    int? CommitsBehind,
    Uri? ChangesUrl);

internal static class UpdateEvaluator
{
    public static UpdateState Evaluate(InstallationChannel channel, string runningVersion, LatestRelease? release, BranchComparison? comparison)
    {
        var isEdge = channel == InstallationChannel.Edge;
        bool? updateAvailable = channel switch
        {
            InstallationChannel.Release => release is not null && IsNewer(release.Version, runningVersion),
            InstallationChannel.Edge => comparison is null ? null : comparison.CommitsBehind > 0,
            _ => null
        };

        return new UpdateState(
            updateAvailable,
            release?.Version,
            release?.PublishedAt,
            release?.Url,
            isEdge ? comparison?.CommitsBehind : null,
            isEdge ? comparison?.Url : null);
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
