namespace Namotion.Interceptor.Testing;

/// <summary>
/// Bounds for waits in tests.
/// </summary>
public static class TestTimeouts
{
    /// <summary>
    /// Bounds a wait for something the test expects to happen, such as work on the thread pool, a timer,
    /// a continuation or a reconnect. A passing test never reaches it; it only caps how long a broken one
    /// takes to fail.
    /// </summary>
    /// <remarks>
    /// Not for a wait that expects nothing to happen, which a generous bound only slows down.
    /// </remarks>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);
}
