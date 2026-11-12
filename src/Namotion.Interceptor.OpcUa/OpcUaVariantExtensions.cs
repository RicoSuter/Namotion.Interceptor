using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

internal static class OpcUaVariantExtensions
{
    /// <summary>
    /// Wraps a value produced by <see cref="OpcUaValueConverter"/> in a variant.
    /// </summary>
    /// <exception cref="ServiceResultException">The value has no OPC UA representation.</exception>
    public static Variant ToVariant(object? value)
    {
        return TryToVariant(value, out var variant)
            ? variant
            : throw ServiceResultException.Create(
                StatusCodes.BadTypeMismatch,
                "A value of type '{0}' has no OPC UA representation.",
                value?.GetType().FullName ?? "null");
    }

    /// <summary>
    /// Wraps a value produced by <see cref="OpcUaValueConverter"/> in a variant, or returns false when
    /// the value has no OPC UA representation.
    /// </summary>
    public static bool TryToVariant(object? value, out Variant variant)
    {
        switch (value)
        {
            // The exact type checks keep sbyte[], which the runtime also matches as byte[], an array.
            // A byte array is a ByteString, matching the data type the server declares for it.
            case byte[] bytes when value.GetType() == typeof(byte[]):
                variant = Variant.From(ByteString.From(bytes));
                return true;

            case byte[][] byteArrays when value.GetType() == typeof(byte[][]):
                variant = Variant.From(Array.ConvertAll(byteArrays, static bytes => ByteString.From(bytes)).ToArrayOf());
                return true;

            // The SDK derives the enumeration type from the static type of the value, which is object here.
            case Enum enumeration:
                variant = Variant.From(EnumValue.From(enumeration, enumeration.GetType()));
                return true;

            case Array { Rank: 1 } array when array.GetType().GetElementType() is { IsEnum: true }:
                var numbers = new int[array.Length];
                for (var i = 0; i < numbers.Length; i++)
                {
                    numbers[i] = Convert.ToInt32(array.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                }

                variant = Variant.From(numbers.ToArrayOf());
                return true;

            default:
                return VariantHelper.TryCastFromWithReflectionFallback(value, out variant);
        }
    }

    /// <summary>
    /// Boxes a variant into the CLR representation <see cref="OpcUaValueConverter"/> works with: arrays as
    /// .NET arrays, <see cref="DateTimeUtc"/> as <see cref="DateTime"/>, <see cref="Uuid"/> as
    /// <see cref="Guid"/> and <see cref="ByteString"/> as a byte array, including arrays of these.
    /// </summary>
    public static object? ToClrValue(this Variant variant)
    {
        // Read without the intermediate box a date and time would otherwise get.
        if (variant.TypeInfo is { BuiltInType: BuiltInType.DateTime, IsScalar: true } &&
            variant.TryGetValue(out DateTimeUtc scalarDateTime))
        {
            return scalarDateTime.ToClrDateTime();
        }

        return variant.AsBoxedObject(Variant.BoxingBehavior.Legacy) switch
        {
            DateTimeUtc dateTime => dateTime.ToClrDateTime(),
            Uuid uuid => (Guid)uuid,
            ByteString byteString => byteString.IsNull ? null : byteString.ToArray(),
            DateTimeUtc[] dateTimes => Array.ConvertAll(dateTimes, static value => value.ToClrDateTime()),
            Uuid[] uuids => Array.ConvertAll(uuids, static value => (Guid)value),
            ByteString[] byteStrings => Array.ConvertAll(byteStrings, static value => value.IsNull ? null! : value.ToArray()),
            var value => value
        };
    }

    /// <summary>
    /// Converts to a <see cref="DateTime"/>, mapping the OPC UA minimum and maximum to
    /// <see cref="DateTime.MinValue"/> and <see cref="DateTime.MaxValue"/> so both round-trip.
    /// </summary>
    public static DateTime ToClrDateTime(this DateTimeUtc value)
    {
        if (value == DateTimeUtc.MinValue)
        {
            return DateTime.MinValue;
        }

        return value == DateTimeUtc.MaxValue ? DateTime.MaxValue : value.ToDateTime();
    }

    /// <summary>
    /// Converts a timestamp to a <see cref="DateTimeOffset"/>, with an unset one as
    /// <see cref="DateTimeOffset.MinValue"/>.
    /// </summary>
    public static DateTimeOffset ToClrTimestamp(this DateTimeUtc value)
    {
        return value == DateTimeUtc.MinValue ? DateTimeOffset.MinValue : value.ToDateTimeOffset();
    }
}
