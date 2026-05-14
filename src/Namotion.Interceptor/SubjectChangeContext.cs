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
    private const long UndefinedTimestampTicks = 0;

    /// <summary>
    /// Timestamp was explicitly set to null (source had no timestamp).
    /// Distinct from <see cref="UndefinedTimestampTicks"/> which triggers a fallback to <see cref="GetTimestampFunction"/>.
    /// Exposed as internal so <c>PropertyWriteContext</c> can recognize the null-scope state.
    /// </summary>
    internal const long NullTimestampTicks = -1;
    
    private static Func<DateTimeOffset>? _customTimestampFunction;
    private static readonly Func<DateTimeOffset> _defaultTimestampFunction = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets a function which retrieves the current timestamp (default is <see cref="DateTimeOffset.UtcNow"/>).
    /// When the default function is in use, the snap fast-path calls <see cref="DateTimeOffset.UtcNow"/> directly,
    /// bypassing delegate dispatch.
    /// </summary>
    public static Func<DateTimeOffset> GetTimestampFunction
    {
        get => _customTimestampFunction ?? _defaultTimestampFunction;
        set => _customTimestampFunction = ReferenceEquals(value, _defaultTimestampFunction) ? null : value;
    }

    /// <summary>
    /// Snaps the current timestamp ticks, bypassing delegate dispatch and the
    /// <see cref="DateTimeOffset"/> wrap when the default function is in use.
    /// (<c>DateTime.UtcNow.Ticks</c> equals <c>DateTimeOffset.UtcNow.UtcTicks</c> for the default path.)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetUtcNowTicks()
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
    private SubjectChangeContext(long changedTimestampUtcTicks, long receivedTimestampUtcTicks, object? source)
    {
        _changedTimestampUtcTicks = changedTimestampUtcTicks;
        _receivedTimestampUtcTicks = receivedTimestampUtcTicks;
        Source = source;
    }

    /// <summary>
    /// Resolves the changed timestamp as UTC ticks, applying sentinel handling and falling back
    /// to <see cref="GetTimestampFunction"/> when no scope is active. Returns 0 when the source
    /// explicitly had no timestamp (preserving "never written" state). This is a method (not a
    /// property) because it may call <c>UtcNow</c> on the fallback path. Within a write chain,
    /// prefer <c>PropertyWriteContext.WriteTimestampForStorage</c> for stability across reads.
    /// Negative scope ticks (either <see cref="NullTimestampTicks"/> or a cascade-shared
    /// encoded null-with-cached-time, see <c>PropertyWriteContext</c>) all map to 0 here.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long ResolveChangedTimestamp()
    {
        if (_changedTimestampUtcTicks < 0)
            return 0; // Explicit null (or shared encoded null): preserve "never written" state

        if (_changedTimestampUtcTicks > 0)
            return _changedTimestampUtcTicks; // Real timestamp from scope

        return GetUtcNowTicks(); // No scope: generate current time
    }

    /// <summary>
    /// Returns the raw scope-state ticks without fallback. Used by <c>PropertyWriteContext</c> for
    /// lazy-snapshot logic: returns 0 (no scope), <see cref="NullTimestampTicks"/>, or positive ticks.
    /// </summary>
    internal static long CurrentChangedTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current._changedTimestampUtcTicks;
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

    /// <summary>
    /// Enters a scope that sets the changed timestamp from raw UTC ticks. Avoids the
    /// <see cref="DateTimeOffset"/> construct + unwrap round-trip on the cascade scope-push hot path
    /// where the caller already has ticks. Accepted values: positive ticks (real timestamp),
    /// <see cref="NullTimestampTicks"/> (explicit null, snap fresh UtcNow when published), or
    /// a value less than <see cref="NullTimestampTicks"/> (cascade-shared encoded null carrying
    /// the trigger's snapped UtcNow as <c>-ticks</c>; see <c>PropertyWriteContext</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static SubjectChangeContextScope WithChangedTimestamp(long changedTicks)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            changedTicks,
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
