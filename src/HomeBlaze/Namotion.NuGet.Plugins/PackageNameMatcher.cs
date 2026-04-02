using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Namotion.NuGet.Plugins;

/// <summary>
/// Matches package names against glob patterns where * matches any characters within a single dot-separated segment.
/// </summary>
public static class PackageNameMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a package name matches a glob pattern.
    /// The pattern uses * to match any characters within a single dot-separated segment.
    /// </summary>
    public static bool IsMatch(string packageName, string pattern)
    {
        var regex = RegexCache.GetOrAdd(pattern, p =>
        {
            var regexPattern = "^" + Regex.Escape(p).Replace("\\*", "[^.]+") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        });
        return regex.IsMatch(packageName);
    }

    /// <summary>
    /// Checks if a package name matches any of the given patterns.
    /// </summary>
    public static bool IsMatchAny(string packageName, IReadOnlyList<string> patterns)
    {
        return patterns.Any(pattern => IsMatch(packageName, pattern));
    }
}
