using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// Interceptor that can intercept and modify property write operations.
/// </summary>
public interface IWriteInterceptor
{
    /// <summary>
    /// Intercepts a property write operation.
    /// </summary>
    /// <typeparam name="TProperty">A hint for the property type. May be <c>object</c> when
    /// values are boxed through non-generic paths (e.g., <c>SetPropertyValueWithInterception</c>).
    /// Use <c>context.Property.Metadata.Type</c> for the actual declared property type.</typeparam>
    /// <param name="context">The write context containing the property reference and values.</param>
    /// <param name="next">The next interceptor in the chain to call. Always forward the received context by
    /// reference. Copying it loses per-call changes, including <see cref="PropertyWriteContext{TProperty}.IsWritten"/>,
    /// and a freshly constructed context also loses the terminal write operation.</param>
    void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next);
}

public delegate void WriteInterceptionDelegate<TProperty>(ref PropertyWriteContext<TProperty> context);

internal interface IWriteCommitGuard
{
    bool TryEnter();

    void Exit();

    bool TryDefer();

    void Resume();
}

/// <summary>
/// Context for a property write operation.
/// <typeparamref name="TProperty"/> is a hint. It may be <c>object</c> when values are
/// boxed through non-generic paths. Use <c>Property.Metadata.Type</c> for the actual
/// declared property type.
/// </summary>
public struct PropertyWriteContext<TProperty>
{
    // Lazy-cache for the write timestamp. One long encodes three states:
    //   == 0    uninitialized; first read calls ResolveAndCacheWriteTimestamp() to populate it.
    //   >  0    real UtcNow ticks.
    //   < -1    explicit-null scope (WithChangedTimestamp(null) was active): carries
    //           -UtcNow.Ticks. Property storage decodes to 0 (the never-written sentinel);
    //           publishing decodes to +ticks so consumers that require a timestamp still get one.
    // The negative encoding lets one field carry both "was null" and the cached ticks.
    // Cascade re-entries skip the resolve entirely: the internal ctor seeds this field with the
    // trigger's already-resolved value.
    private long _writeTimestamp;

    // Per-call terminal state flows by ref through the chain.
    internal Action<IInterceptorSubject, TProperty>? Terminal;

    internal Func<IInterceptorSubject, TProperty>? ReadValue;

    internal IWriteTerminalCoordinator? TerminalCoordinator;

    internal StructuralWriteLease? StructuralLease;

    internal IWriteCommitGuard? CommitGuard;

    internal object? CommittedLifecycleJournal;

    // Paired with Property.Subject so the terminal can stamp its executor directly.
    internal readonly InterceptorExecutor Executor;

    private TProperty _currentValue;
    private TProperty _newValue;
    private TProperty _terminalValue = default!;
    private bool _terminalEntered;
    private ChangeOrigin _terminalOrigin;
    private bool _isTerminalOriginResolved;

    internal bool IsTerminalCommitted;

    /// <summary>
    /// The subject's commit revision assigned by the terminal write, or 0 when the write did not
    /// commit. Monotonic per subject, not comparable across subjects.
    /// </summary>
    internal long Revision;

    /// <summary>
    /// Gets the property to write a value to.
    /// </summary>
    public PropertyReference Property { get; }

    /// <summary>
    /// Gets the current property value.
    /// </summary>
    public TProperty CurrentValue => _currentValue;

    /// <summary>
    /// Gets or sets the value forwarded toward the terminal. Terminal entry freezes its current
    /// value for storage and publication. Assignments after <c>next</c> returns affect only this
    /// context's unwind state and do not change <see cref="GetFinalValue"/>.
    /// </summary>
    public TProperty NewValue
    {
        get => _newValue;
        set => _newValue = value;
    }

    /// <summary>
    /// Gets or sets whether the write was performed.
    /// Set to true by the write action when the value is actually written.
    /// </summary>
    public bool IsWritten { get; set; }

    // Attempted origin paired with the source value until terminal finalization.
    private AttemptedOrigin _attempted;

    /// <summary>
    /// The origin of this write. Before the terminal write executes this is the attempted
    /// origin (what the caller declared when setting the pending origin); when the terminal write lands (the same
    /// point <see cref="IsWritten"/> becomes true) it is finalized: a stamped origin whose
    /// final value differs from the sent value becomes Local, because the stored value was
    /// computed locally rather than taken from the source.
    /// </summary>
    public ChangeOrigin Origin => _attempted.Origin;

    // Construction consumes this property's thread-static pending origin.
    internal PropertyWriteContext(InterceptorExecutor executor, PropertyReference property, TProperty currentValue, TProperty newValue)
    {
        Executor = executor;
        Property = property;
        _currentValue = currentValue;
        _newValue = newValue;
        IsWritten = false;
        _writeTimestamp = 0;
        PendingOrigin.TryConsume(in property, out _attempted);
    }

    // Cascade re-entry supplies its trigger's resolved timestamp and stabilized value.
    internal PropertyWriteContext(InterceptorExecutor executor, PropertyReference property, TProperty currentValue, TProperty newValue, long rawTimestamp)
    {
        Executor = executor;
        Property = property;
        _currentValue = currentValue;
        _newValue = newValue;
        IsWritten = false;
        _writeTimestamp = rawTimestamp;
        PendingOrigin.TryConsume(in property, out _attempted);
    }

    internal TProperty FreezeNewValue()
    {
        if (_terminalEntered)
            throw new InvalidOperationException("The write terminal can only be entered once.");
        _terminalEntered = true;
        return _terminalValue = _newValue;
    }

    internal void PrepareTerminalState()
    {
        _terminalOrigin = ResolveFinalOrigin();
        _isTerminalOriginResolved = true;
        _ = WriteTimestampRawForCommit;
    }

    internal void SetTerminalPredecessor(TProperty value) => _currentValue = value;

    /// <summary>
    /// Gets the timestamp stamped on the property by this write, or <c>null</c> if the write used
    /// an explicit null-timestamp scope (the property is stamped as never-written).
    ///
    /// Lazily resolved on first access and cached for the remainder of the write so all consumers
    /// (terminal write, change-event publishers, transaction capture, derived recalc) observe the
    /// same value regardless of read order. Source: an active <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/>
    /// scope (when set), or <see cref="SubjectChangeContext.GetTimestampFunction"/> when no scope is active.
    /// </summary>
    public DateTimeOffset? WriteTimestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var ticks = _writeTimestamp;
            if (ticks == 0) ticks = ResolveAndCacheWriteTimestamp();
            return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        }
    }

    /// <summary>
    /// The timestamp to use when publishing this write as a change event. Always a real value,
    /// even when the write used an explicit null-timestamp scope (consumers expect a value).
    /// Same lazy-resolve semantics as <see cref="WriteTimestamp"/>.
    /// </summary>
    internal DateTimeOffset WriteTimestampForPublishing
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var ticks = _writeTimestamp;
            if (ticks == 0) ticks = ResolveAndCacheWriteTimestamp();
            return new DateTimeOffset(ticks > 0 ? ticks : -ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Raw encoded cache value (see the <c>_writeTimestamp</c> field comment for the encoding).
    /// Threaded into cascade dependents' contexts so they share the trigger's captured time.
    /// Same lazy-resolve semantics as <see cref="WriteTimestamp"/>.
    /// </summary>
    internal long WriteTimestampRaw
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var ticks = _writeTimestamp;
            if (ticks == 0) ticks = ResolveAndCacheWriteTimestamp();
            return ticks;
        }
    }

    /// <summary>
    /// Resolves the timestamp needed by a committed write. A generated write before the subject
    /// has crossed any attachment boundary still consumes a terminal revision for race safety, but
    /// it preserves the historical pre-publication behavior of having no write timestamp. Before
    /// generated structural coordination existed, those writes did not observe timestamp scopes.
    /// </summary>
    internal long WriteTimestampRawForCommit
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var ticks = _writeTimestamp;
            if (ticks != 0)
            {
                return ticks;
            }

            return Executor.SuppressGeneratedPrepublicationTimestamp
                ? 0
                : ResolveAndCacheWriteTimestamp();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ResolveAndCacheWriteTimestamp()
    {
        // Branch order picks "no scope first" as the conceptual default for app writes; the
        // delta vs alternative orderings is sub-noise in benchmarks (stylistic, not measured).
        // Cascade dependents skip this entirely via the internal ctor.
        var scopeTicks = SubjectChangeContext.CurrentChangedTimestamp;
        long result;
        if (scopeTicks == 0)
        {
            result = SubjectChangeContext.CaptureTimestamp(); // No scope
        }
        else if (scopeTicks > 0)
        {
            result = scopeTicks; // Real timestamp from scope
        }
        else
        {
            // scopeTicks == NullTimestampSentinel (-1): explicit-null scope. Capture UtcNow and
            // encode as negative so storage decodes to 0 (never-written sentinel) while
            // publishing decodes to a real DateTimeOffset for change-event consumers.
            result = -SubjectChangeContext.CaptureTimestamp();
        }
        _writeTimestamp = result;
        return result;
    }

    /// <summary>
    /// Gets the value frozen when the write entered its terminal. Assigning <see cref="NewValue"/>
    /// after <c>next</c> returns does not change this value or built-in publication. Must only be used
    /// after the <c>next</c> call in the write interceptor.
    /// </summary>
    /// <returns>The property value.</returns>
    public TProperty GetFinalValue()
    {
        if (_terminalEntered)
        {
            return _terminalValue;
        }

        var property = Property;
        var metadata = property.Metadata;
        return metadata.IsDerived
            ? (TProperty)metadata.GetValue?.Invoke(property.Subject)!
            : NewValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChangeOrigin GetFinalOrigin()
    {
        return _isTerminalOriginResolved ? _terminalOrigin : ResolveFinalOrigin();
    }

    private ChangeOrigin ResolveFinalOrigin()
    {
        if (_attempted.Origin.Kind == ChangeOriginKind.Local)
        {
            return default;
        }

        // A derived property's stored value is recomputed by its getter, never literally the sent value,
        // so a stamped origin never survives. Demoted without invoking the getter, which must not run
        // here (this executes under the executor's terminal lock).
        if (Property.Metadata.IsDerived)
        {
            return default;
        }

        // Survive only when the sent value was faithfully stored. The 'is TProperty' pattern unboxes
        // SentValue for the exact type against the unboxed NewValue. A null sent value must be handled
        // explicitly ('null is TProperty' is always false), else a legitimately stored null would demote
        // to Local and defeat echo suppression. A box the pattern rejects falls back to the setter's own
        // unbox (see SentValueEqualsAfterUnbox); a box the setter would reject demotes.
        var finalValue = GetFinalValue();
        var survives = _attempted.SentValue is TProperty typedSentValue
            ? EqualityComparer<TProperty>.Default.Equals(typedSentValue, finalValue)
            : _attempted.SentValue is null
                ? finalValue is null
                : SentValueEqualsAfterUnbox(_attempted.SentValue, finalValue);

        return survives ? _attempted.Origin : default;
    }

    /// <summary>
    /// Finalizes <see cref="Origin"/> at the terminal write (right after <see cref="IsWritten"/>
    /// becomes true). A stamped origin survives only when the stored value is exactly the value the
    /// source sent; otherwise the value was computed locally and the origin becomes Local.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void FinalizeOrigin()
    {
        if (GetFinalOrigin().Kind == ChangeOriginKind.Local)
        {
            _attempted = default;
        }
    }

    /// <summary>
    /// Fallback comparison mirroring the setter's own unbox: the is-pattern is type-strict, but the CLR
    /// unboxes an enum and its underlying integral type interchangeably, like the generated setter's cast
    /// (OPC UA delivers enums as boxed integers, so such a write stores faithfully and must keep its
    /// origin). A boxed underlying integer is first coerced to the enum so a nullable enum survives too:
    /// (DeviceMode)boxedInt unboxes leniently but (DeviceMode?)boxedInt throws, so without the coercion a
    /// faithfully-stored nullable enum would demote to Local and defeat echo suppression. On a genuinely
    /// incompatible box the cast throws and this method catches it, demoting to Local (safe for survival:
    /// a value the setter could not have produced does not deserve to keep the source's origin). The catch
    /// arm is unreachable for chain writes (a box the setter would reject never produced a successful
    /// write) and only guards hand-constructed sent values. Kept out of the inlined finalize path: an
    /// exception handler would make FinalizeOrigin uninlinable for every write.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SentValueEqualsAfterUnbox(object sentValue, TProperty newValue)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(TProperty)) ?? typeof(TProperty);
        if (targetType.IsEnum && sentValue.GetType() == Enum.GetUnderlyingType(targetType))
        {
            sentValue = Enum.ToObject(targetType, sentValue);
        }

        try
        {
            return EqualityComparer<TProperty>.Default.Equals((TProperty)sentValue, newValue);
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }
}
