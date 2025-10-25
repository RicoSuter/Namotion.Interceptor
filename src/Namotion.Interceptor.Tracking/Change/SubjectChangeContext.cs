using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public readonly struct SubjectChangeContext
{
    [ThreadStatic]
    private static SubjectChangeContext _current;
    
    private readonly DateTimeOffset? _changedTimestamp;
    public readonly DateTimeOffset? ReceivedTimestamp;
    public readonly object? Source;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SubjectChangeContext(DateTimeOffset? changed, DateTimeOffset? received, object? source)
    {
        _changedTimestamp = changed;
        ReceivedTimestamp = received;
        Source = source;
    }

    /// <summary>Gets the changed timestamp from the thread-local context or falls back to <see cref="SubjectChangeContext.GetTimestampFunction"/>.</summary>
    public DateTimeOffset ChangedTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _changedTimestamp ?? GetTimestampFunction();
    }
    
    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.Now"/>).
    /// </summary>
    public static Func<DateTimeOffset> GetTimestampFunction { get; set; } = () => DateTimeOffset.Now;

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
        _current = new SubjectChangeContext(changed, previousState.ReceivedTimestamp, previousState.Source);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>Enters a scope that sets only the received timestamp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithReceivedTimestamp(DateTimeOffset? received)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(previousState._changedTimestamp, received, previousState.Source);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>Enters a scope that sets only the mutation source.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithSource(object? source)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(previousState._changedTimestamp, previousState.ReceivedTimestamp, source);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>Enters a scope that sets source, changed and received timestamps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithState(object? source, DateTimeOffset? changed, DateTimeOffset? received)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(changed, received, source);
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