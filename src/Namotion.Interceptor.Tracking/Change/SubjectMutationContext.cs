namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectMutationContext
{
    [ThreadStatic] private static object? _currentSource;
    [ThreadStatic] private static DateTimeOffset? _currentTimestamp;
    
    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.Now"/>).
    /// </summary>
    public static Func<DateTimeOffset> GetTimestampFunction { get; set; } = () => DateTimeOffset.Now;

    /// <summary>
    /// Changes the current timestamp in the async local context until the scope is disposed.
    /// </summary>
    /// <param name="timestamp">The timestamp to set in the context.</param>
    /// <param name="action">The action.</param>
    public static T ApplyChangesWithTimestamp<T>(DateTimeOffset? timestamp, Func<T> action)
    {
        _currentTimestamp = timestamp;
        try
        {
            return action();
        }
        finally
        {
            _currentTimestamp = null;
        }
    }
    
    /// <summary>
    /// Changes the current timestamp in the async local context until the scope is disposed.
    /// </summary>
    /// <param name="timestamp">The timestamp to set in the context.</param>
    /// <param name="action">The action.</param>
    public static void ApplyChangesWithTimestamp(DateTimeOffset? timestamp, Action action)
    {
        _currentTimestamp = timestamp;
        try
        {
            action();
        }
        finally
        {
            _currentTimestamp = null;
        }
    }

    /// <summary>
    /// Executes the action and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="action">The action</param>
    public static void ApplyChangesWithSource(object source, Action action)
    {
        _currentSource = source;
        try
        {
            action();
        }
        finally
        {
            _currentSource = null;
        }
    }

    /// <summary>
    /// Gets the current timestamp from the async local context or fallback to calling <see cref="GetTimestampFunction"/>.
    /// </summary>
    /// <returns>The current timestamp.</returns>
    public static DateTimeOffset GetCurrentTimestamp()
    {
        return _currentTimestamp ?? GetTimestampFunction();
    }

    /// <summary>
    /// Gets the current source which is doing the mutation.
    /// </summary>
    /// <returns>The source or null (unknown).</returns>
    internal static object? GetCurrentSource()
    {
        return _currentSource;
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
        _currentSource = source;
        try
        {
            property.SetValue(valueFromSource);
        }
        finally
        {
            _currentSource = null;
        }
    }
}