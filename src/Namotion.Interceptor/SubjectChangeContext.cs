using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public readonly struct SubjectChangeContext
{
    private readonly long _changedTimestamp;
    private readonly long _receivedTimestamp;

    [ThreadStatic]
    private static SubjectChangeContext _current;
    
    /// <summary>
    /// No timestamp was set (default struct value). Falls back to <see cref="GetTimestampFunction"/>.
    /// </summary>
    private const long UndefinedTimestampSentinel = 0;

    /// <summary>
    /// Timestamp was explicitly set to null (source had no timestamp).
    /// Distinct from <see cref="UndefinedTimestampSentinel"/> which triggers a fallback to <see cref="GetTimestampFunction"/>.
    /// Exposed as internal so <c>PropertyWriteContext</c> can recognize the null-scope state.
    /// </summary>
    internal const long NullTimestampSentinel = -1;
    
    private static Func<DateTimeOffset>? _customTimestampFunction;
    private static readonly Func<DateTimeOffset> _defaultTimestampFunction = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.UtcNow"/>).
    /// When the default function is in use, the fast-path calls <see cref="DateTimeOffset.UtcNow"/> directly,
    /// bypassing delegate dispatch.
    /// </summary>
    public static Func<DateTimeOffset> GetTimestampFunction
    {
        get => _customTimestampFunction ?? _defaultTimestampFunction;
        set => _customTimestampFunction = ReferenceEquals(value, _defaultTimestampFunction) ? null : value;
    }

    /// <summary>
    /// Captures the current timestamp, bypassing delegate dispatch and the
    /// <see cref="DateTimeOffset"/> wrap when the default function is in use.
    /// (<c>DateTime.UtcNow.Ticks</c> equals <c>DateTimeOffset.UtcNow.UtcTicks</c> for the default path.)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long CaptureTimestamp()
    {
        // Snapshot the field once: a concurrent setter resetting it to null between
        // the null-check and the invocation would otherwise produce a NullReferenceException.
        var fn = _customTimestampFunction;
        return fn is null
            ? DateTime.UtcNow.Ticks
            : fn().UtcTicks;
    }

    /// <summary>Gets the current change context.</summary>
    public static SubjectChangeContext Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SubjectChangeContext(long changedTimestamp, long receivedTimestamp)
    {
        _changedTimestamp = changedTimestamp;
        _receivedTimestamp = receivedTimestamp;
    }

    /// <summary>
    /// Resolves the changed timestamp as UTC ticks, applying sentinel handling and falling back
    /// to <see cref="GetTimestampFunction"/> when no scope is active. Returns 0 when the source
    /// explicitly had no timestamp (preserving "never written" state). This is a method (not a
    /// property) because it may call <c>UtcNow</c> on the fallback path. Within a write chain,
    /// prefer <c>PropertyWriteContext.WriteTimestamp</c> for stability across reads.
    /// Negative scope ticks (either <see cref="NullTimestampSentinel"/> or a cascade-shared
    /// encoded null-with-cached-time, see <c>PropertyWriteContext</c>) all map to 0 here.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long ResolveChangedTimestamp()
    {
        if (_changedTimestamp < 0)
            return 0; // Explicit null (or shared encoded null): preserve "never written" state

        if (_changedTimestamp > 0)
            return _changedTimestamp; // Real timestamp from scope

        return CaptureTimestamp(); // No scope: generate current time
    }

    /// <summary>
    /// Returns the raw scope-state ticks without fallback. Used by <c>PropertyWriteContext</c> for
    /// lazy-capture logic: returns 0 (no scope), <see cref="NullTimestampSentinel"/>, or positive ticks.
    /// </summary>
    internal static long CurrentChangedTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current._changedTimestamp;
    }

    /// <summary>Gets the received timestamp from the thread-local context, or null if not set.</summary>
    public DateTimeOffset? ReceivedTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _receivedTimestamp != UndefinedTimestampSentinel
            ? new DateTimeOffset(_receivedTimestamp, TimeSpan.Zero)
            : null;
    }

    /// <summary>
    /// Enters a scope so every write inside publishes with the same timestamp. Use this when
    /// multiple writes belong to one logical event. Pass <c>null</c> when the source has no
    /// timestamp; the property stays marked as never-written for storage, but change-event
    /// consumers still receive a captured timestamp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithChangedTimestamp(DateTimeOffset? changed)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            changed?.UtcTicks ?? NullTimestampSentinel,
            previousState._receivedTimestamp);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>
    /// Enters a scope that sets the changed timestamp and, when <paramref name="received"/> is
    /// non-null, the received timestamp. A null <paramref name="received"/> preserves the ambient
    /// received timestamp, exactly as <see cref="WithChangedTimestamp"/> does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithTimestamps(DateTimeOffset? changed, DateTimeOffset? received)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            changed?.UtcTicks ?? NullTimestampSentinel,
            received?.UtcTicks ?? previousState._receivedTimestamp);
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
