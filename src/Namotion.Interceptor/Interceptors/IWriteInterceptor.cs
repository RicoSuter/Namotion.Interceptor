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
    /// <param name="next">The next interceptor in the chain to call.</param>
    void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next);
}

public delegate void WriteInterceptionDelegate<TProperty>(ref PropertyWriteContext<TProperty> context);

/// <summary>
/// Context for a property write operation.
/// <typeparamref name="TProperty"/> is a hint. It may be <c>object</c> when values are
/// boxed through non-generic paths. Use <c>Property.Metadata.Type</c> for the actual
/// declared property type.
/// </summary>
public struct PropertyWriteContext<TProperty>
{
    // Lazy-cache for the write timestamp. After resolve, one long carries three states:
    //   == 0    uninitialized; first read calls ResolveAndCacheWriteTimestamp() to populate it.
    //           Also the value seeded by the public constructor for non-cascade writes.
    //   >  0    real ticks; both storage and publishing return this value.
    //   < -1    explicit-null scope: WithChangedTimestamp(null) was active during the write.
    //           Carries the snapped UtcNow ticks as -ticks. WriteTimestampForStorage returns 0
    //           (the "never-written" sentinel preserved on the property), but
    //           WriteTimestampForPublishing decodes -ticks and returns the positive
    //           DateTimeOffset so connectors (OPC UA, MQTT, queue, observable) that require a
    //           concrete timestamp still receive one. Exactly -1 is never cached -- it's only a
    //           scope-side sentinel that gets resolved to -UtcNow.Ticks on first read.
    // The negative-encoding lets one 8-byte field carry both "this was null" and the cached
    // UtcNow ticks, avoiding a second flag field. Cascade re-entries skip the lazy resolve
    // entirely: the internal constructor seeds this field with the trigger's already-resolved
    // value, so dependents inherit the trigger's snapped time without an active scope push.
    // See ResolveAndCacheWriteTimestamp() for the encode path and WriteTimestampForStorage /
    // WriteTimestampForPublishing / WriteTimestamp / WriteTimestampRaw for the four decode paths.
    private long _writeTimestamp;

    /// <summary>
    /// Gets the property to write a value to.
    /// </summary>
    public PropertyReference Property { get; }

    /// <summary>
    /// Gets the current property value.
    /// </summary>
    public TProperty CurrentValue { get; }

    /// <summary>
    /// Gets the new value to write (might be different than the value returned by calling the
    /// getter after the write, use <see cref="GetFinalValue"/> for that).
    /// </summary>
    public TProperty NewValue { get; set; }

    /// <summary>
    /// Gets or sets whether the write was performed.
    /// Set to true by the write action when the value is actually written.
    /// </summary>
    public bool IsWritten { get; set; }

    public PropertyWriteContext(PropertyReference property, TProperty currentValue, TProperty newValue)
    {
        Property = property;
        CurrentValue = currentValue;
        NewValue = newValue;
        IsWritten = false;
        _writeTimestamp = 0;
    }

    /// <summary>
    /// Internal constructor for cascade re-entry: pre-populates the cache with the trigger's
    /// already-resolved raw timestamp, so the dependent's write does not need to lazy-resolve
    /// (and therefore does not need an active <c>WithChangedTimestamp</c> scope to share state
    /// with the trigger). Pass 0 to leave the cache uninitialized (the default lazy behavior).
    /// </summary>
    internal PropertyWriteContext(PropertyReference property, TProperty currentValue, TProperty newValue, long preResolvedRawTimestamp)
    {
        Property = property;
        CurrentValue = currentValue;
        NewValue = newValue;
        IsWritten = false;
        _writeTimestamp = preResolvedRawTimestamp;
    }

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
    /// Raw ticks for property storage: <c>0</c> for the explicit null-timestamp scope (never-written sentinel),
    /// otherwise the resolved real ticks. Same lazy-resolve semantics as <see cref="WriteTimestamp"/>.
    /// </summary>
    internal long WriteTimestampForStorage
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var ticks = _writeTimestamp;
            if (ticks == 0) ticks = ResolveAndCacheWriteTimestamp();
            return ticks > 0 ? ticks : 0;
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
    /// The raw encoded cached value used by the derived-cascade re-entry to share this write's
    /// snapped time with downstream dependents: positive ticks for a real timestamp, or a value
    /// less than <see cref="SubjectChangeContext.NullTimestampTicks"/> for an explicit-null write
    /// carrying the trigger's snapped <c>-UtcNow.Ticks</c>. Threading this value through the
    /// internal <c>PropertyWriteContext</c> constructor lets every dependent in the cascade
    /// publish the same timestamp as the trigger, even under an explicit-null scope, without
    /// pushing a thread-local <see cref="SubjectChangeContext"/> scope.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ResolveAndCacheWriteTimestamp()
    {
        // Three scope states resolve here. The branch order picks "no scope first" since
        // that's the default for app-level writes (any setter call outside an explicit
        // WithChangedTimestamp scope); positive scope (connector imports that pass a source
        // timestamp) pays one extra comparison. The perf delta between orderings is sub-noise
        // in benchmarks -- this is a stylistic choice that matches the conceptual default for
        // the typical library user, not a measured win, and either ordering is defensible.
        // The cascade re-entry path does not run this resolve at all: cascade dependents'
        // contexts are pre-populated with the trigger's resolved value via the internal
        // PropertyWriteContext constructor.
        var scopeTicks = SubjectChangeContext.CurrentChangedTimestamp;
        long result;
        if (scopeTicks == 0)
        {
            result = SubjectChangeContext.GetUtcNowTicks(); // No scope
        }
        else if (scopeTicks > 0)
        {
            result = scopeTicks; // Real timestamp from scope
        }
        else
        {
            // scopeTicks == NullTimestampTicks (-1): explicit-null scope. Snap UtcNow now and
            // encode as negative so storage decodes to 0 (never-written sentinel) while
            // publishing decodes to a real DateTimeOffset for change-event consumers.
            result = -SubjectChangeContext.GetUtcNowTicks();
        }
        _writeTimestamp = result;
        return result;
    }

    /// <summary>
    /// Reads the current property value (might be different from <see cref="NewValue"/> if the property is derived).
    /// Must only be used after the 'next()' call in the write interceptor.
    /// </summary>
    /// <returns>The property value.</returns>
    public TProperty GetFinalValue() => Property.Metadata.IsDerived ?
        (TProperty)Property.Metadata.GetValue?.Invoke(Property.Subject)! :
        NewValue;
}
