namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectMutationContext
{
    [ThreadStatic]
    private static object? _currentChangingSource;
    
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
    public static IDisposable BeginTimestampScope(DateTimeOffset? timestamp)
    {
        if (timestamp is not null)
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
    
    /// <summary>
    /// Gets the current source which is doing the mutation.
    /// </summary>
    /// <returns>The source or null (unknown).</returns>
    internal static object? GetCurrentSource()
    {
        return _currentChangingSource;
    }

    /// <summary>
    /// Checks if the property change is from the specified source.
    /// </summary>
    /// <param name="change">The property change.</param>
    /// <param name="source">The source find.</param>
    /// <returns>The result.</returns>
    public static bool IsChangingFromSource(this SubjectPropertyChange change, object source)
    {
        return change.Source == source;
    }
    
    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source.</param>
    /// <param name="valueFromSource">The value</param>
    public static void SetValueFromSource(this PropertyReference property, object source, object? valueFromSource)
    {
        _currentChangingSource = source;
        try
        {
            property.SetValue(valueFromSource);
        }
        finally
        {
            _currentChangingSource = null;
        }
    }

    /// <summary>
    /// Executes the action and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="action">The action</param>
    public static void ApplyChangesFromSource(object source, Action action)
    {
        _currentChangingSource = source;
        try
        {
            action();
        }
        finally
        {
            _currentChangingSource = null;
        }
    }

    private class ResetDisposableDisposable : IDisposable
    {
        public void Dispose()
        {
            CurrentTimestamp.Value = null;
        }
    }
}