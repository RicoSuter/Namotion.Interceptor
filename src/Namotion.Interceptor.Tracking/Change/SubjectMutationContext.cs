using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectMutationContext
{
    [ThreadStatic] private static object? _currentSource;
    [ThreadStatic] private static DateTimeOffset? _currentChangedTimestamp;
    [ThreadStatic] private static DateTimeOffset? _currentReceivedTimestamp;
    
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
        var previousTimestamp = _currentChangedTimestamp;
        _currentChangedTimestamp = timestamp;
        try
        {
            return action();
        }
        finally
        {
            _currentChangedTimestamp = previousTimestamp;
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
        var previousTimestamp = _currentChangedTimestamp;
        _currentChangedTimestamp = timestamp;
        try
        {
            action();
        }
        finally
        {
            _currentChangedTimestamp = previousTimestamp;
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
        var previousSource = _currentSource;
        _currentSource = source;
        try
        {
            action();
        }
        finally
        {
            _currentSource = previousSource;
        }
    }

    /// <summary>
    /// Gets the changed timestamp from the async local context or fallback to calling <see cref="GetTimestampFunction"/>.
    /// </summary>
    /// <returns>The current timestamp.</returns>
    public static DateTimeOffset GetChangedTimestamp()
    {
        return _currentChangedTimestamp ?? GetTimestampFunction();
    }

    /// <summary>
    /// Gets the received timestamp from the async local context or null when unknown (usually when not set from source).
    /// </summary>
    /// <returns>The current timestamp.</returns>
    public static DateTimeOffset? TryGetReceivedTimestamp()
    {
        return _currentReceivedTimestamp;
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
    /// Sets the value of the property and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source.</param>
    /// <param name="changedTimestamp">The changed timestamp to set in the context.</param>
    /// <param name="receivedTimestamp">The received timestamp to set in the context.</param>
    /// <param name="valueFromSource">The value</param>
    public static void SetValueFromSource(this PropertyReference property, object source, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, object? valueFromSource)
    {        
        var previousChangedTimestamp = _currentChangedTimestamp;
        var previousReceivedTimestamp = _currentChangedTimestamp;
        var previousSource = _currentSource;
        _currentChangedTimestamp = changedTimestamp;
        _currentReceivedTimestamp = receivedTimestamp;
        _currentSource = source;
        try
        {
            property.Metadata.SetValue?.Invoke(property.Subject, valueFromSource);
        }
        finally
        {
            _currentChangedTimestamp = previousChangedTimestamp;
            _currentReceivedTimestamp = previousReceivedTimestamp;
            _currentSource = previousSource;
        }
    }
}