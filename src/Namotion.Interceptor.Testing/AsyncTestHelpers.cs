namespace Namotion.Interceptor.Testing;

/// <summary>
/// Helper methods for asynchronous test assertions with active waiting.
/// </summary>
public static class AsyncTestHelpers
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Waits until the specified condition becomes true, polling at regular intervals.
    /// Throws TimeoutException if the condition is not met within the timeout period.
    /// </summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <param name="pollInterval">Interval between condition checks. Defaults to 50ms.</param>
    /// <param name="message">Optional message to include in the timeout exception.</param>
    /// <exception cref="TimeoutException">Thrown when the condition is not met within the timeout.</exception>
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? message = null)
    {
        var actualTimeout = timeout ?? DefaultTimeout;
        var actualPollInterval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTime.UtcNow + actualTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(actualPollInterval).ConfigureAwait(false);
        }

        var errorMessage = string.IsNullOrEmpty(message)
            ? $"Condition was not met within {actualTimeout.TotalSeconds:F1} seconds."
            : $"{message} (timed out after {actualTimeout.TotalSeconds:F1} seconds)";

        throw new TimeoutException(errorMessage);
    }
}
