namespace Namotion.Interceptor.Testing;

/// <summary>
/// Helper methods for asynchronous test assertions with active waiting.
/// </summary>
public static class AsyncTestHelpers
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Waits until the specified condition becomes true, polling at regular intervals.
    /// Throws TimeoutException if the condition is not met within the timeout period.
    /// </summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <param name="pollInterval">Interval between condition checks. Defaults to 100ms.</param>
    /// <param name="message">Optional message to include in the timeout exception.</param>
    /// <exception cref="TimeoutException">Thrown when the condition is not met within the timeout.</exception>
    [AssertionMethod]
    public static Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? message = null)
    {
        return WaitUntilAsync(condition, () => message, timeout, pollInterval);
    }

    /// <summary>
    /// Waits until the specified condition becomes true, polling at regular intervals.
    /// Throws TimeoutException if the condition is not met within the timeout period.
    /// </summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="message">
    /// Builds the message included in the timeout exception. Called only on timeout, so it can report
    /// the state at that point.
    /// </param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <param name="pollInterval">Interval between condition checks. Defaults to 100ms.</param>
    /// <exception cref="TimeoutException">Thrown when the condition is not met within the timeout.</exception>
    [AssertionMethod]
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        Func<string?> message,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var actualTimeout = timeout ?? DefaultTimeout;
        var actualPollInterval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTime.UtcNow + actualTimeout;

        while (DateTime.UtcNow < deadline)
        {
            // Memory barrier ensures we see updates from other threads (e.g., OPC UA callbacks)
            // This prevents stale reads that can cause tests to timeout on CI under load
            Thread.MemoryBarrier();

            if (condition())
            {
                return;
            }

            await Task.Delay(actualPollInterval).ConfigureAwait(false);
        }

        var messageText = message();
        var errorMessage = string.IsNullOrEmpty(messageText)
            ? $"Condition was not met within {actualTimeout.TotalSeconds:F1} seconds."
            : $"{messageText} (timed out after {actualTimeout.TotalSeconds:F1} seconds)";

        throw new TimeoutException(errorMessage);
    }
}
