using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change.Performance;

/// <summary>
/// The operations on a value of one type held in an <see cref="InlineValueStorage"/>, so the storage needs no type
/// tests or reflection to box or compare it.
/// </summary>
internal abstract class InlineValueOperations
{
    private protected InlineValueOperations()
    {
    }

    /// <summary>
    /// Gets the stored type.
    /// </summary>
    public abstract Type Type { get; }

    /// <summary>
    /// Boxes the value held in <paramref name="storage"/>, which must store this type.
    /// </summary>
    public abstract object? Box(in InlineValueStorage storage);

    /// <summary>
    /// Returns <c>object.Equals(value, Box(storage))</c>, boxing the value held in <paramref name="storage"/>, which
    /// must store this type, only when the result could differ otherwise.
    /// </summary>
    public abstract bool ValueEquals(in InlineValueStorage storage, object? value);
}

/// <summary>
/// The <see cref="InlineValueOperations"/> of <typeparamref name="TValue"/>, one instance per type.
/// </summary>
internal sealed class InlineValueOperations<TValue> : InlineValueOperations
{
    // Native AOT pre-initializes this class only while it has no constructor arguments and no static state besides
    // this instance, and a nested holder class would not be pre-initialized either; otherwise every inline read or
    // comparison pays a class constructor check first.
    internal static readonly InlineValueOperations<TValue> Instance = new();

    private InlineValueOperations()
    {
    }

    public override Type Type => typeof(TValue);

    public override object? Box(in InlineValueStorage storage) => storage.Read<TValue>();

    // A value of another runtime type, null included, decides by its own Equals, so it takes the boxed comparison.
    public override bool ValueEquals(in InlineValueStorage storage, object? value) =>
        IsEqualsKnownToAgreeWithObjectEquals() && value is TValue typed
            ? EqualityComparer<TValue>.Default.Equals(typed, storage.Read<TValue>())
            : Equals(value, Box(storage));

    // object.Equals(value, stored) calls value.Equals(object). Only for these types is Equals(TValue), which the
    // default comparer calls, known to answer the same; a user struct may implement the two differently. Inlined so
    // the type tests fold to a constant per TValue.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsEqualsKnownToAgreeWithObjectEquals()
    {
        if (typeof(TValue).IsPrimitive || typeof(TValue).IsEnum)
        {
            return true;
        }

        if (typeof(TValue) == typeof(decimal) || typeof(TValue) == typeof(DateTime))
        {
            return true;
        }

        return typeof(TValue) == typeof(DateTimeOffset) || typeof(TValue) == typeof(TimeSpan) ||
            typeof(TValue) == typeof(Guid);
    }
}
