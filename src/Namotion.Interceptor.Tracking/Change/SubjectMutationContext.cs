namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectMutationContext
{
    private static readonly ResetDisposableDisposable ResetDisposableInstance = new();
    private static readonly AsyncLocal<DateTimeOffset?> CurrentTimestamp = new();
    
    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.Now"/>).
    /// </summary>
    public static Func<DateTimeOffset> GetTimestampFunction { get; set; } = () => DateTimeOffset.Now;
    
    /// <summary>
    /// Changes the current timestamp in the async local context until the scope is disposed.
    /// </summary>
    /// <param name="timestamp">The timestamp to set in the context.</param>
    public static IDisposable BeginTimestampScope(DateTimeOffset timestamp)
    {
        CurrentTimestamp.Value = timestamp;
        return ResetDisposableInstance;
    }
    
    /// <summary>
    /// Gets the current timestamp from the async local context or fallback to calling <see cref="GetTimestampFunction"/>.
    /// </summary>
    /// <returns>The current timestamp.</returns>
    public static DateTimeOffset GetCurrentTimestamp()
    {
        return CurrentTimestamp.Value ?? GetTimestampFunction();
    }

    private class ResetDisposableDisposable : IDisposable
    {
        public void Dispose()
        {
            CurrentTimestamp.Value = null;
        }
    }
}