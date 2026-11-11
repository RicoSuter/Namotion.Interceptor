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

    // A notification closes itself about eight seconds after it opens, so a bound near that would let a notification
    // that must not show pass once it has closed.

    /// <summary>
    /// Bounds an assertion that something is absent and stays absent. Make it right after the point where the thing
    /// would have appeared, ideally after a signal that the action has completed.
    /// </summary>
    public const float Absence = 2000;
}
