using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public readonly struct SubjectChangeContext
{
    private readonly long _changedTimestamp;
    private readonly long _receivedTimestamp;

    public readonly object? Source;
    
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
    private SubjectChangeContext(long changedTimestamp, long receivedTimestamp, object? source)
    {
        _changedTimestamp = changedTimestamp;
        _receivedTimestamp = receivedTimestamp;
        Source = source;
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
            previousState._receivedTimestamp,
            previousState.Source);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>Enters a scope that sets only the mutation source.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithSource(object? source)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            previousState._changedTimestamp,
            previousState._receivedTimestamp,
            source);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>
    /// Enters a scope that marks writes inside it as local origin: resets the source to null while
    /// preserving the ambient changed and received timestamps. Used around framework-invoked
    /// consequence callbacks (generated property hooks, INotifyPropertyChanged raises, derived
    /// recalculations) so their writes flow to bound sources like any local write.
    /// Forward-compatibility seam for the typed ChangeOrigin discriminator (#342): encodes local
    /// origin as Source = null today; a future version sets Kind = Local without changing this
    /// signature or any call site.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithLocalOrigin()
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            previousState._changedTimestamp,
            previousState._receivedTimestamp,
            null);
        return new SubjectChangeContextScope(previousState);
    }

    /// <summary>Enters a scope that sets source, changed and received timestamps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithState(object? source, DateTimeOffset? changed, DateTimeOffset? received)
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            changed?.UtcTicks ?? NullTimestampSentinel,
            received?.UtcTicks ?? UndefinedTimestampSentinel,
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
