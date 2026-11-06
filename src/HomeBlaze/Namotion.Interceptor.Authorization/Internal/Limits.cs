namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>The bounds that keep compiling and evaluating a policy finite on any role set and any graph.</summary>
internal static class Limits
{
    // A chain of single parents shallower than this is followed without a visited set, so it allocates nothing; deeper, a revisit ends a cycle.
    public const int CycleTrackingDepth = 256;

    // A subject's paths are listed up to this many, so a subject shared through many routes does not make every evaluation enumerate them all.
    public const int MaxPaths = 64;

    // The path search takes at most this many parent links per allowed path, so a dense component without a root cannot keep it searching.
    public const int ParentLinksPerPath = 64;

    // Includes nest at most this deep, so resolving a role set never recurses deeply.
    public const int MaxIncludeDepth = 64;
}
