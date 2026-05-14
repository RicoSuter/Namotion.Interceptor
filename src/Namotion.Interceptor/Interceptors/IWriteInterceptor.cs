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
    // Encoding: 0 = uninitialized, >0 = resolved real ticks (storage == publish),
    // <0 = explicit null scope (storage = 0, publish = -value).
    // Negative encoding lets one long carry both "stored as null" and the publish ticks,
    // keeping the struct compact.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ResolveAndCacheWriteTimestamp()
    {
        var scopeTicks = SubjectChangeContext.CurrentChangedTimestamp;
        long result;
        if (scopeTicks > 0)
        {
            result = scopeTicks;
        }
        else if (scopeTicks == SubjectChangeContext.NullTimestampTicks)
        {
            result = -SubjectChangeContext.GetUtcNowTicks(); // negative = explicit-null encoding
        }
        else
        {
            result = SubjectChangeContext.GetUtcNowTicks();
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
