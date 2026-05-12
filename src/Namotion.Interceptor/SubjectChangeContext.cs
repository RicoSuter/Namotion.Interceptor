using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public readonly struct SubjectChangeContext
{
    private readonly long _changedTimestampUtcTicks;
    private readonly long _receivedTimestampUtcTicks;

    public readonly object? Source;
    
    [ThreadStatic]
    private static SubjectChangeContext _current;
    
    /// <summary>
    /// No timestamp was set (default struct value). Falls back to <see cref="GetTimestampFunction"/>.
    /// </summary>
    internal const long UndefinedTimestampTicks = 0;

    /// <summary>
    /// Timestamp was explicitly set to null (source had no timestamp).
    /// Distinct from <see cref="UndefinedTimestampTicks"/> which triggers a fallback to <see cref="GetTimestampFunction"/>.
    /// </summary>
    internal const long NullTimestampTicks = -1;
    
    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.UtcNow"/>).
    /// </summary>
    public static Func<DateTimeOffset> GetTimestampFunction { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Gets the current change context.</summary>
    public static SubjectChangeContext Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SubjectChangeContext(long changedTimestampUtcTicks, long receivedTimestampUtcTicks, object? source)
    {
        _changedTimestampUtcTicks = changedTimestampUtcTicks;
        _receivedTimestampUtcTicks = receivedTimestampUtcTicks;
        Source = source;
    }

    /// <summary>
    /// Returns the raw scope-state ticks without any fallback or processing.
    /// Use this when implementing lazy snapshot logic (see <c>PropertyWriteContext.WriteTimestamp</c>):
    /// returns <c>0</c> (no scope), <c>-1</c> (explicit null scope), or a positive value (explicit timestamp scope).
    /// </summary>
    internal static long RawCurrentChangedTimestampTicks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current._changedTimestampUtcTicks;
    }

    /// <summary>
    /// Gets the changed timestamp as raw UTC ticks for paths that do not have a <c>PropertyWriteContext</c>
    /// available (e.g., lifecycle attach handlers). Within a write chain, prefer
    /// <c>PropertyWriteContext.WriteTimestampStorageTicks</c> for stability across multiple reads.
    /// Returns 0 when the source explicitly had no timestamp (preserving "never written" state).
    /// </summary>
    internal long ChangedTimestampUtcTicks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_changedTimestampUtcTicks == NullTimestampTicks)
                return 0;
            if (_changedTimestampUtcTicks != UndefinedTimestampTicks)
                return _changedTimestampUtcTicks;
            return GetTimestampFunction().UtcTicks;
        }
    }

    /// <summary>
    /// Gets the changed timestamp as a DateTimeOffset for paths that do not have a <c>PropertyWriteContext</c>
    /// available (e.g., lifecycle attach handlers). Within a write chain, prefer
    /// <c>PropertyWriteContext.WriteTimestampForPublishing</c> for stability across multiple reads
    /// (this getter falls back to <see cref="GetTimestampFunction"/> on every call when no scope is active,
    /// so multiple reads within one write can drift by microseconds).
    /// </summary>
    internal DateTimeOffset ChangedTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _changedTimestampUtcTicks > 0
            ? new DateTimeOffset(_changedTimestampUtcTicks, TimeSpan.Zero)
            : GetTimestampFunction();
    }

    /// <summary>Gets the received timestamp from the thread-local context, or null if not set.</summary>
    public DateTimeOffset? ReceivedTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _receivedTimestampUtcTicks != UndefinedTimestampTicks
            ? new DateTimeOffset(_receivedTimestampUtcTicks, TimeSpan.Zero)
            : null;
    }

    /// <summary>Enters a scope that sets only the changed timestamp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithChangedTimestamp(DateTimeOffset? changed)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            changed?.UtcTicks ?? NullTimestampTicks,
            previousState._receivedTimestampUtcTicks,
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
            changed?.UtcTicks ?? NullTimestampTicks,
            received?.UtcTicks ?? UndefinedTimestampTicks,
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
