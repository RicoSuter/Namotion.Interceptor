using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Timeouts of the Playwright assertions, in milliseconds.
/// </summary>
internal static class AssertionTimeouts
{
    /// <summary>
    /// Bounds an assertion that waits for something to happen, including a wait until something is gone. It is the
    /// default timeout of every assertion.
    /// </summary>
    public static readonly float Default = (float)TestTimeouts.Default.TotalMilliseconds;

    /// <summary>
    /// Bounds an assertion that something is absent and stays absent. Make it right after the point where the thing
    /// would have appeared, ideally after a signal that the action has completed.
    /// </summary>
    // An absent thing passes at once, so the bound only matters while the thing shows. It stays below the eight seconds
    // after which a notification closes itself, which would let a notification that must not show pass.
    public const float Absence = 5000;
}
