using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

internal static class OpcUaVariantExtensions
{
    /// <summary>
    /// Wraps a value produced by <see cref="OpcUaValueConverter"/> in a variant. A byte array becomes a
    /// <see cref="ByteString"/>, matching the data type the server declares for byte array properties,
    /// instead of an array of bytes.
    /// </summary>
    public static Variant ToVariant(object? value)
    {
        return value switch
        {
            byte[] bytes => Variant.From(ByteString.From(bytes)),
            byte[][] byteArrays => Variant.From(Array.ConvertAll(byteArrays, static bytes => ByteString.From(bytes)).ToArrayOf()),
            _ => VariantHelper.CastFromWithReflectionFallback(value)
        };
    }

    /// <summary>
    /// Boxes a variant into the CLR representation <see cref="OpcUaValueConverter"/> works with: arrays as
    /// .NET arrays, <see cref="DateTimeUtc"/> as <see cref="DateTime"/>, <see cref="Uuid"/> as
    /// <see cref="Guid"/> and <see cref="ByteString"/> as a byte array, including arrays of these.
    /// </summary>
    public static object? ToClrValue(this Variant variant)
    {
        return variant.AsBoxedObject(Variant.BoxingBehavior.Legacy) switch
        {
            DateTimeUtc dateTime => dateTime.ToDateTime(),
            Uuid uuid => (Guid)uuid,
            ByteString byteString => byteString.IsNull ? null : byteString.ToArray(),
            DateTimeUtc[] dateTimes => Array.ConvertAll(dateTimes, static value => value.ToDateTime()),
            Uuid[] uuids => Array.ConvertAll(uuids, static value => (Guid)value),
            ByteString[] byteStrings => Array.ConvertAll(byteStrings, static value => value.IsNull ? null! : value.ToArray()),
            var value => value
        };
    }
}
