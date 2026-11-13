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

            // DateTime.MaxValue is Unspecified, so converting it to UTC would shift it below the OPC UA
            // maximum in time zones east of UTC, and it would no longer map back to DateTime.MaxValue.
            case DateTime dateTime:
                variant = Variant.From(ToDateTimeUtc(dateTime));
                return true;

            case DateTime[] dateTimes:
                variant = Variant.From(Array.ConvertAll(dateTimes, ToDateTimeUtc).ToArrayOf());
                return true;

            // The SDK derives the enumeration type from the static type of the value, which is object here.
            // OPC UA enumerations are Int32; a value outside that range has no representation.
            case Enum enumeration:
                if (!TryGetInt32(enumeration, out var number))
                {
                    variant = default;
                    return false;
                }

                variant = Variant.From(EnumValue.From(number, enumeration.GetType()));
                return true;

            case Array { Rank: 1 } array when array.GetType().GetElementType() is { IsEnum: true } elementType:
                if (!TryGetInt32Array(array, Type.GetTypeCode(elementType), out var numbers))
                {
                    variant = default;
                    return false;
                }

                variant = Variant.From(numbers.ToArrayOf());
                return true;

            default:
                return VariantHelper.TryCastFromWithReflectionFallback(value, out variant);
        }
    }

    private static DateTimeUtc ToDateTimeUtc(DateTime value)
    {
        return value.Ticks == DateTime.MaxValue.Ticks ? DateTimeUtc.MaxValue : new DateTimeUtc(value);
    }

    /// <summary>
    /// Gets the Int32 an enumeration value is sent as. Unboxes to the underlying type instead of going
    /// through <see cref="Convert"/>, which boxes again. A UInt32 value keeps its bits, as the SDK's
    /// mapping from Int32 back to the enumeration expects; a 64-bit value must fit into Int32.
    /// </summary>
    private static bool TryGetInt32(object? enumeration, out int value)
    {
        if (enumeration is not Enum)
        {
            value = 0;
            return false;
        }

        switch (Type.GetTypeCode(enumeration.GetType()))
        {
            case TypeCode.SByte:
                value = (sbyte)enumeration;
                return true;
            case TypeCode.Byte:
                value = (byte)enumeration;
                return true;
            case TypeCode.Int16:
                value = (short)enumeration;
                return true;
            case TypeCode.UInt16:
                value = (ushort)enumeration;
                return true;
            case TypeCode.Int32:
                value = (int)enumeration;
                return true;
            case TypeCode.UInt32:
                value = unchecked((int)(uint)enumeration);
                return true;
            case TypeCode.Int64:
                var signed = (long)enumeration;
                value = unchecked((int)signed);
                return signed is >= int.MinValue and <= int.MaxValue;
            case TypeCode.UInt64:
                var unsigned = (ulong)enumeration;
                value = unchecked((int)unsigned);
                return unsigned <= int.MaxValue;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// Gets the Int32 array an enumeration array is sent as, with the same mapping as
    /// <see cref="TryGetInt32"/>. Reads the array as its underlying type, which the runtime allows for
    /// enumeration arrays, so no element is boxed.
    /// </summary>
    private static bool TryGetInt32Array(Array array, TypeCode underlyingType, out int[] numbers)
    {
        numbers = new int[array.Length];
        switch (underlyingType)
        {
            case TypeCode.SByte:
                var signedBytes = (sbyte[])array;
                for (var i = 0; i < numbers.Length; i++)
                {
                    numbers[i] = signedBytes[i];
                }

                return true;
            case TypeCode.Byte:
                var bytes = (byte[])array;
                for (var i = 0; i < numbers.Length; i++)
                {
                    numbers[i] = bytes[i];
                }

                return true;
            case TypeCode.Int16:
                var shorts = (short[])array;
                for (var i = 0; i < numbers.Length; i++)
                {
                    numbers[i] = shorts[i];
                }

                return true;
            case TypeCode.UInt16:
                var unsignedShorts = (ushort[])array;
                for (var i = 0; i < numbers.Length; i++)
                {
                    numbers[i] = unsignedShorts[i];
                }

                return true;
            case TypeCode.Int32:
                ((int[])array).CopyTo(numbers, 0);
                return true;
            case TypeCode.UInt32:
                var unsignedInts = (uint[])array;
                for (var i = 0; i < numbers.Length; i++)
                {
                    numbers[i] = unchecked((int)unsignedInts[i]);
                }

                return true;
            case TypeCode.Int64:
                var longs = (long[])array;
                for (var i = 0; i < numbers.Length; i++)
                {
                    if (longs[i] is < int.MinValue or > int.MaxValue)
                    {
                        return false;
                    }

                    numbers[i] = (int)longs[i];
                }

                return true;
            case TypeCode.UInt64:
                var unsignedLongs = (ulong[])array;
                for (var i = 0; i < numbers.Length; i++)
                {
                    if (unsignedLongs[i] > int.MaxValue)
                    {
                        return false;
                    }

                    numbers[i] = (int)unsignedLongs[i];
                }

                return true;
            default:
                return false;
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
