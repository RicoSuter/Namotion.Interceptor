using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public readonly struct SubjectChangeContext
{
    [ThreadStatic]
    private static SubjectChangeContext _current;

    private readonly long _changedTimestampUtcTicks;
    private readonly long _receivedTimestampUtcTicks;

    public readonly object? Source;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SubjectChangeContext(long changedTimestampUtcTicks, long receivedTimestampUtcTicks, object? source)
    {
        _changedTimestampUtcTicks = changedTimestampUtcTicks;
        _receivedTimestampUtcTicks = receivedTimestampUtcTicks;
        Source = source;
    }

    /// <summary>Gets the changed timestamp from the thread-local context or falls back to <see cref="SubjectChangeContext.GetTimestampFunction"/>.</summary>
    public DateTimeOffset ChangedTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _changedTimestampUtcTicks != 0
            ? new DateTimeOffset(_changedTimestampUtcTicks, TimeSpan.Zero)
            : GetTimestampFunction();
    }

    /// <summary>Gets the received timestamp from the thread-local context, or null if not set.</summary>
    public DateTimeOffset? ReceivedTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _receivedTimestampUtcTicks != 0
            ? new DateTimeOffset(_receivedTimestampUtcTicks, TimeSpan.Zero)
            : null;
    }

    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.UtcNow"/>).
    /// </summary>
    private static Func<DateTimeOffset> GetTimestampFunction { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Gets the current change context.</summary>
    public static SubjectChangeContext Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _current;
    }

    /// <summary>Enters a scope that sets only the changed timestamp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithChangedTimestamp(DateTimeOffset? changed)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            changed?.UtcTicks ?? 0,
            previousState._receivedTimestampUtcTicks,
            previousState.Source);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>Enters a scope that sets only the received timestamp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithReceivedTimestamp(DateTimeOffset? received)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            previousState._changedTimestampUtcTicks,
            received?.UtcTicks ?? 0,
            previousState.Source);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>Enters a scope that sets only the mutation source.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithSource(object? source)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            previousState._changedTimestampUtcTicks,
            previousState._receivedTimestampUtcTicks,
            source);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>Enters a scope that sets source, changed and received timestamps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithState(object? source, DateTimeOffset? changed, DateTimeOffset? received)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            changed?.UtcTicks ?? 0,
            received?.UtcTicks ?? 0,
            source);
        return new SubjectChangeContextScope(previousState);
    }

    public readonly ref struct SubjectChangeContextScope
    {
        private readonly SubjectChangeContext _previousState;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SubjectChangeContextScope(SubjectChangeContext previousState) => _previousState = previousState;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => _current = _previousState;
    }
}
