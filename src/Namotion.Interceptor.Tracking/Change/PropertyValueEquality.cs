using System.Collections.Concurrent;
using System.Reflection;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Compares two boxed property values using the declared property type's default equality
/// (<see cref="EqualityComparer{T}"/>, so an <see cref="IEquatable{T}"/> implementation is honored
/// even when the type does not override <c>object.Equals</c>), matching what
/// <c>PropertyValueEqualityCheckHandler</c> does in its generic frame.
///
/// Correction detection and delivery revalidation MUST use the same equality as that handler: a boxed
/// <c>object.Equals</c> would treat two <see cref="IEquatable{T}"/> instances the handler considers
/// equal as divergent, so every echo the handler suppresses would synthesize a fresh correction, a
/// self-sustaining loop. Casting the boxed value to the declared type also unboxes an enum's
/// underlying integral type to the enum (the OPC UA wire shape), so a numerically equal
/// underlying-typed value compares as the enum, not as a foreign box.
///
/// This unbox equality is kept in sync with the origin survival check (<c>SentValueEqualsAfterUnbox</c>
/// in the core write interceptor, which cannot reference this assembly): both unwrap
/// <see cref="Nullable{T}"/> and coerce a boxed underlying integer to the enum. They diverge only on a
/// genuinely incompatible box: detection (here) lets the cast throw to surface a defect, survival
/// catches it and demotes to Local.
/// </summary>
internal static class PropertyValueEquality
{
    private static readonly MethodInfo TypedEqualsMethod =
        typeof(PropertyValueEquality).GetMethod(nameof(TypedEquals), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, Func<object?, object?, bool>> Comparers = new();

    /// <summary>
    /// Returns true when <paramref name="left"/> and <paramref name="right"/> are equal under
    /// <paramref name="propertyType"/>'s default equality. Two nulls are equal; a null and a non-null
    /// are not (the typed comparison is only reached for two non-null boxes, so a value-type cast
    /// never unboxes a null).
    /// </summary>
    public static bool Equals(Type propertyType, object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        // Compare on the non-nullable underlying type. Both operands are non-null here, and boxing a
        // Nullable<T> already yields a box of T, so the runtime values are unaffected. The unwrap
        // matters for the enum/underlying case: unboxing a boxed underlying integer to the enum uses
        // the CLR's leniency (DeviceMode)(object)1, but unboxing it to the nullable enum
        // (DeviceMode?)(object)1 throws, because that leniency does not extend through Nullable<T>.
        var comparisonType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        return Comparers.GetOrAdd(comparisonType, static type =>
            TypedEqualsMethod.MakeGenericMethod(type).CreateDelegate<Func<object?, object?, bool>>())(left, right);
    }

    private static bool TypedEquals<T>(object? left, object? right) =>
        EqualityComparer<T>.Default.Equals((T)left!, (T)right!);
}
