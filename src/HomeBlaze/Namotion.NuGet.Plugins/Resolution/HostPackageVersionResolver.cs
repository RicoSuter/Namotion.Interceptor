namespace Namotion.NuGet.Plugins.Resolution;

/// <summary>
/// Resolves common version ranges when multiple plugins need the same host-classified package.
/// </summary>
internal static class HostPackageVersionResolver
{
    public record VersionResolutionResult(
        bool Success,
        Dictionary<string, global::NuGet.Versioning.VersionRange> ResolvedRanges,
        IReadOnlyList<NuGetPluginConflict> Conflicts);

    public static VersionResolutionResult ResolveVersions(
        Dictionary<string, List<(string PluginName, global::NuGet.Versioning.VersionRange Range)>> requirements)
    {
        var resolvedRanges = new Dictionary<string, global::NuGet.Versioning.VersionRange>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<NuGetPluginConflict>();

        foreach (var (packageName, pluginRanges) in requirements)
        {
            if (pluginRanges.Count == 0)
            {
                continue;
            }

            var combined = pluginRanges[0].Range;
            var allCompatible = true;

            for (int i = 1; i < pluginRanges.Count; i++)
            {
                var common = global::NuGet.Versioning.VersionRange.CommonSubSet(
                    new[] { combined, pluginRanges[i].Range });

                if (common == null ||
                    common.Equals(global::NuGet.Versioning.VersionRange.None) ||
                    (common.HasLowerBound && common.HasUpperBound && common.MinVersion > common.MaxVersion))
                {
                    allCompatible = false;
                    var requestedBy = string.Join(", ",
                        pluginRanges.Select(range => $"{range.PluginName} ({range.Range})"));

                    conflicts.Add(new NuGetPluginConflict(
                        packageName,
                        pluginRanges[i].Range.ToNormalizedString(),
                        combined.ToNormalizedString(),
                        requestedBy));

                    break;
                }

                combined = common;
            }

            if (allCompatible)
            {
                resolvedRanges[packageName] = combined;
            }
        }

        return new VersionResolutionResult(conflicts.Count == 0, resolvedRanges, conflicts);
    }
}
