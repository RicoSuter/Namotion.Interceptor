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

/// <summary>
/// Whether a newer build is available, and the data backing that decision.
/// </summary>
/// <remarks>
/// <see cref="UpdateAvailable"/> is <c>null</c> when availability is unknown, for example on the
/// <see cref="InstallationChannel.Development"/> channel or when the running version does not parse as SemVer.
/// <see cref="CommitsBehind"/> and <see cref="ChangesUrl"/> are only populated on the
/// <see cref="InstallationChannel.Edge"/> channel.
/// </remarks>
internal sealed record UpdateState(
    bool? UpdateAvailable,
    string? LatestVersion,
    DateTimeOffset? LatestReleaseDate,
    Uri? LatestReleaseUrl,
    int? CommitsBehind,
    Uri? ChangesUrl);

internal static class UpdateEvaluator
{
    /// <summary>
    /// Derives the <see cref="UpdateState"/> for the given channel from an already fetched release and branch
    /// comparison. Callers only invoke this after their requests succeeded, so a <c>null</c>
    /// <paramref name="release"/> means the repository has no published release, not a failed request.
    /// </summary>
    public static UpdateState Evaluate(InstallationChannel channel, string runningVersion, LatestRelease? release, BranchComparison? comparison)
    {
        var isEdge = channel == InstallationChannel.Edge;
        bool? updateAvailable = channel switch
        {
            InstallationChannel.Release => EvaluateRelease(release, runningVersion),
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
    /// Returns false when there is no published release, null when the running version does not parse as SemVer
    /// (so no comparison can be made), and otherwise whether the release is newer than the running version.
    /// </summary>
    private static bool? EvaluateRelease(LatestRelease? release, string runningVersion)
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
