namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>The bounds that keep compiling and evaluating a policy finite on any role set and any graph.</summary>
internal static class Limits
{
    // An evaluation tries this many times to read the graph between two equal even structure versions. Between
    // attempts it waits with SpinWait, which spins first and later also yields the thread, so a burst of registry
    // changes usually ends before the evaluation fails closed; a registry that changes without pause can still make
    // it fail closed, which only a read under the registry's lock would avoid.
    public const int MaxConsistencyAttempts = 40;

    // The visibility walk tracks every subject from this depth on, so a deep single-parent chain reached from several branches is explored once.
    public const int WalkVisitedTrackingDepth = 32;

    // Path enumeration follows a branchless chain of single parents this deep without a visited set; deeper, a revisit ends a cycle.
    public const int ChainCycleTrackingDepth = 256;

    // A subject's paths are listed up to this many, so a subject shared through many routes does not make every evaluation enumerate them all.
    public const int MaxPaths = 64;

    // The path search takes at most this many parent links per allowed path, so a dense component without a root cannot keep it searching.
    public const int ParentLinksPerPath = 64;

    // Includes nest at most this deep, so resolving a role set never recurses deeply.
    public const int MaxIncludeDepth = 64;
}
