using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectChangeContextExtensions
{
    /// <summary>
    /// Sets the value of the property, arming the pending origin so the resulting write carries the
    /// given <paramref name="origin"/> instead of scoping an ambient source. This is the intent-level
    /// entry point for connectors and appliers that cannot reach the internal pending-origin slot.
    /// When <paramref name="receivedTimestamp"/> is null the ambient received timestamp is preserved
    /// (see <see cref="SubjectChangeContext.WithTimestamps"/>); only a non-null value replaces it.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="origin">The origin to stamp on the resulting change.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp, or null to preserve the ambient value.</param>
    /// <param name="value">The value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromOrigin(
        this PropertyReference property,
        ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
        object? value)
    {
        using (SubjectChangeContext.WithTimestamps(changedTimestamp, receivedTimestamp))
        using (PendingOrigin.Set(property, origin, value))
        {
            property.Metadata.SetValue?.Invoke(property.Subject, value);
        }
    }

    /// <summary>
    /// Sets the value of the property from the given source, changed and received timestamp.
    /// Forwards to <see cref="SetValueFromOrigin"/> with a <see cref="ChangeOrigin.FromSource"/> origin.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="source">The source.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp.</param>
    /// <param name="valueFromSource">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromSource(
        this PropertyReference property,
        object source, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
        object? valueFromSource) =>
        property.SetValueFromOrigin(ChangeOrigin.FromSource(source), changedTimestamp, receivedTimestamp, valueFromSource);
}