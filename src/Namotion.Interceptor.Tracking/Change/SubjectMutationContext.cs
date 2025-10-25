using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectMutationContext
{
    [ThreadStatic] private static (DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, object? source) _currentContext;
    
    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.Now"/>).
    /// </summary>
    public static Func<DateTimeOffset> GetTimestampFunction { get; set; } = () => DateTimeOffset.Now;

    /// <summary>
    /// Changes the current timestamp in the async local context until the scope is disposed.
    /// </summary>
    /// <param name="timestamp">The timestamp to set in the context.</param>
    /// <param name="action">The action.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ApplyChangesWithChangedTimestamp<T>(DateTimeOffset? timestamp, Func<T> action)
    {
        var previousTimestamp = _currentContext.changedTimestamp;
        _currentContext.changedTimestamp = timestamp;
        try
        {
            return action();
        }
        finally
        {
            _currentContext.changedTimestamp = previousTimestamp;
        }
    }
    
    /// <summary>
    /// Changes the current changed timestamp in the async local context until the scope is disposed.
    /// </summary>
    /// <param name="timestamp">The timestamp to set in the context.</param>
    /// <param name="action">The action.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyChangesWithChangedTimestamp(DateTimeOffset? timestamp, Action action)
    {
        var previousTimestamp = _currentContext.changedTimestamp;
        _currentContext.changedTimestamp = timestamp;
        try
        {
            action();
        }
        finally
        {
            _currentContext.changedTimestamp = previousTimestamp;
        }
    }

    /// <summary>
    /// Executes the action and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="action">The action</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyChangesWithSource(object? source, Action action)
    {
        var previousSource = _currentContext.source;
        _currentContext.source = source;
        try
        {
            action();
        }
        finally
        {
            _currentContext.source = previousSource;
        }
    }

    /// <summary>
    /// Gets the changed timestamp from the async local context or fallback to calling <see cref="GetTimestampFunction"/>.
    /// </summary>
    /// <returns>The current timestamp.</returns>
    public static DateTimeOffset GetChangedTimestamp()
    {
        return _currentContext.changedTimestamp ?? GetTimestampFunction();
    }

    /// <summary>
    /// Gets the received timestamp from the async local context or null when unknown (usually when not set from source).
    /// </summary>
    /// <returns>The current timestamp.</returns>
    public static DateTimeOffset? TryGetReceivedTimestamp()
    {
        return _currentContext.receivedTimestamp;
    }

    /// <summary>
    /// Gets the current source which is doing the mutation.
    /// </summary>
    /// <returns>The source or null (unknown).</returns>
    internal static object? GetCurrentSource()
    {
        return _currentContext.source;
    }

    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source.</param>
    /// <param name="changedTimestamp">The changed timestamp to set in the context.</param>
    /// <param name="receivedTimestamp">The received timestamp to set in the context.</param>
    /// <param name="valueFromSource">The value</param>
    public static void SetValueFromSource(this PropertyReference property, object source, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, object? valueFromSource)
    {        
        var previousContext = _currentContext;
        _currentContext = (changedTimestamp, receivedTimestamp, source);
        try
        {
            property.Metadata.SetValue?.Invoke(property.Subject, valueFromSource);
        }
        finally
        {
            _currentContext = previousContext;
        }
    }
}