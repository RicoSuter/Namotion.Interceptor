using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectChangeContext
{
    [ThreadStatic] private static State _state;

    internal struct State
    {
        public readonly DateTimeOffset? ChangedTimestamp;
        public readonly DateTimeOffset? ReceivedTimestamp;
        public readonly object? Source;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public State(DateTimeOffset? changed, DateTimeOffset? received, object? source)
        {
            ChangedTimestamp = changed;
            ReceivedTimestamp = received;
            Source = source;
        }
    }
    
    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.Now"/>).
    /// </summary>
    public static Func<DateTimeOffset> GetTimestampFunction { get; set; } = () => DateTimeOffset.Now;

    /// <summary>Gets the changed timestamp from the thread-local context or falls back to <see cref="GetTimestampFunction"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset GetChangedTimestamp() => _state.ChangedTimestamp ?? GetTimestampFunction();

    /// <summary>Gets the received timestamp from the thread-local context, or null when unknown.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset? TryGetReceivedTimestamp() => _state.ReceivedTimestamp;

    /// <summary>Gets the current source which is doing the mutation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? GetCurrentSource() => _state.Source;
    
    /// <summary>Enters a scope that sets only the changed timestamp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Scope WithChangedTimestamp(DateTimeOffset? changed)
    {
        var previousState = _state;
        _state = new State(changed, previousState.ReceivedTimestamp, previousState.Source);
        return new Scope(previousState);
    }

    /// <summary>Enters a scope that sets only the received timestamp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Scope WithReceivedTimestamp(DateTimeOffset? received)
    {
        var previousState = _state;
        _state = new State(previousState.ChangedTimestamp, received, previousState.Source);
        return new Scope(previousState);
    }

    /// <summary>Enters a scope that sets only the mutation source.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Scope WithSource(object? source)
    {
        var previousState = _state;
        _state = new State(previousState.ChangedTimestamp, previousState.ReceivedTimestamp, source);
        return new Scope(previousState);
    }

    /// <summary>Enters a scope that sets source, changed and received timestamps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Scope With(object? source, DateTimeOffset? changed, DateTimeOffset? received)
    {
        var previousState = _state;
        _state = new State(changed, received, source);
        return new Scope(previousState);
    }

    /// <summary>
    /// Sets the value of the property from the given source, changed and received timestamp.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="source">The source.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp.</param>
    /// <param name="valueFromSource">The value.</param>
    public static void SetValueFromSource(
        this PropertyReference property, 
        object source, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, 
        object? valueFromSource)
    {
        using (With(source, changedTimestamp, receivedTimestamp))
        {
            property.Metadata.SetValue?.Invoke(property.Subject, valueFromSource);
        }
    }

    public readonly ref struct Scope
    {
        private readonly State _previousState;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Scope(State previousState)
        {
            _previousState = previousState;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => _state = _previousState;
    }
}
