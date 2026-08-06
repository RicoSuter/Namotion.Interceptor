using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Every rule must also be listed in AnalyzerReleases.Unshipped.md, or RS2008 fails the build.
/// </summary>
internal static class Diagnostics
{
    public const string Category = "Namotion.Interceptor";
}
