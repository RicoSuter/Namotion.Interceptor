using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Interceptor.Modbus.Mapping;

/// <summary>
/// Converts the raw wire bytes of one mapping into the boxed property value, or <c>null</c> when the value is not available.
/// </summary>
internal delegate object? ModbusValueReader(ReadOnlySpan<byte> raw, int scaleFactorExponent);

internal static class ModbusValueConverters
{
    private const int MaximumDecimalExponent = 28;

    // 2^96: the smallest float magnitude that no longer fits in a decimal.
    private const float DecimalFloatLimit = 79228162514264337593543950336f;

    private static readonly object True = true;
    private static readonly object False = false;

    private static readonly decimal[] PowersOfTen = CreatePowersOfTen();
    private static readonly decimal[] NegativePowersOfTen = CreateNegativePowersOfTen();

    public static ModbusValueReader Create(ModbusRegisterAttribute attribute, Type propertyType, string propertyPath, bool hasDynamicScale)
    {
        var underlyingType = Nullable.GetUnderlyingType(propertyType);
        var targetType = underlyingType ?? propertyType;
        var isNullable = underlyingType is not null || !propertyType.IsValueType;

        var dataType = attribute.DataType;
        var isScaled = hasDynamicScale || attribute.Scale is not 1.0;

        ValidateNotAvailableValue(attribute, isNullable, propertyPath);

        switch (dataType)
        {
            case ModbusDataType.String:
                RequireTarget(propertyPath, targetType == typeof(string), dataType, targetType);
                RequireUnscaled(propertyPath, isScaled, targetType);
                return static (raw, _) => ModbusRegisterCodec.ReadString(raw);

            case ModbusDataType.Boolean:
                RequireTarget(propertyPath, targetType == typeof(bool), dataType, targetType);
                RequireUnscaled(propertyPath, isScaled, targetType);
                return static (raw, _) => raw[0] != 0 ? True : False;

            case ModbusDataType.F32:
                return CreateFloatReader(attribute, targetType, propertyPath, isNullable, hasDynamicScale, isScaled);

            default:
                return IsScalableTarget(targetType)
                    ? CreateScaledIntegerReader(attribute, targetType, propertyPath, hasDynamicScale)
                    : CreateUnscaledIntegerReader(attribute, targetType, propertyPath, isScaled);
        }
    }

    private static void ValidateNotAvailableValue(ModbusRegisterAttribute attribute, bool isNullable, string propertyPath)
    {
        if (attribute.NotAvailableValue == ModbusNotAvailableValue.None)
        {
            return;
        }

        if (!isNullable)
        {
            throw ModbusConfigurationException.ForMapping(propertyPath, "NotAvailableValue requires a nullable property type.");
        }

        if (attribute.DataType is ModbusDataType.Boolean or ModbusDataType.F32 or ModbusDataType.String)
        {
            throw ModbusConfigurationException.ForMapping(propertyPath, $"NotAvailableValue is not supported for {attribute.DataType}.");
        }
    }

    private static bool IsScalableTarget(Type targetType)
        => targetType == typeof(decimal) || targetType == typeof(double) || targetType == typeof(float);

    private static ModbusValueReader CreateFloatReader(
        ModbusRegisterAttribute attribute, Type targetType, string propertyPath,
        bool isNullable, bool hasDynamicScale, bool isScaled)
    {
        var wordOrder = attribute.WordOrder;
        var staticScale = attribute.Scale;
        if (targetType == typeof(float))
        {
            return (raw, exponent) =>
            {
                var value = ModbusRegisterCodec.ReadSingle(raw, wordOrder);
                return isScaled ? (float)ApplyDoubleScale(value, hasDynamicScale, staticScale, exponent) : value;
            };
        }

        if (targetType == typeof(double))
        {
            return (raw, exponent) => ApplyDoubleScale(ModbusRegisterCodec.ReadSingle(raw, wordOrder), hasDynamicScale, staticScale, exponent);
        }

        if (targetType == typeof(decimal))
        {
            var decimalScale = ToDecimalScale(propertyPath, staticScale);
            return (raw, exponent) =>
            {
                var value = ModbusRegisterCodec.ReadSingle(raw, wordOrder);
                // Also true for NaN, which fails every comparison.
                if (isNullable && !(Math.Abs(value) < DecimalFloatLimit))
                {
                    return null;
                }

                return (decimal)value * GetDecimalScale(hasDynamicScale, decimalScale, exponent);
            };
        }

        throw ModbusConfigurationException.ForMapping(propertyPath, $"F32 requires a float, double or decimal property, not {targetType.Name}.");
    }

    private static ModbusValueReader CreateScaledIntegerReader(
        ModbusRegisterAttribute attribute, Type targetType, string propertyPath, bool hasDynamicScale)
    {
        var dataType = attribute.DataType;
        var wordOrder = attribute.WordOrder;
        var notAvailableValue = attribute.NotAvailableValue;
        var staticScale = attribute.Scale;

        if (targetType == typeof(decimal))
        {
            var decimalScale = ToDecimalScale(propertyPath, staticScale);
            return (raw, exponent) => ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue)
                ? null
                : (decimal)ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) * GetDecimalScale(hasDynamicScale, decimalScale, exponent);
        }

        if (targetType == typeof(double))
        {
            return (raw, exponent) => ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue)
                ? null
                : ApplyDoubleScale((double)ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder), hasDynamicScale, staticScale, exponent);
        }

        return (raw, exponent) => ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue)
            ? null
            : (float)ApplyDoubleScale((double)ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder), hasDynamicScale, staticScale, exponent);
    }

    private static ModbusValueReader CreateUnscaledIntegerReader(
        ModbusRegisterAttribute attribute, Type targetType, string propertyPath, bool isScaled)
    {
        var dataType = attribute.DataType;
        var wordOrder = attribute.WordOrder;
        var notAvailableValue = attribute.NotAvailableValue;

        RequireUnscaled(propertyPath, isScaled, targetType);

        if (targetType == typeof(bool))
        {
            return (raw, _) =>
            {
                if (ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue))
                {
                    return null;
                }

                return ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) != Int128.Zero ? True : False;
            };
        }

        if (targetType.IsEnum)
        {
            RequireIntegralRange(propertyPath, dataType, Enum.GetUnderlyingType(targetType));
            return (raw, _) => ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue)
                ? null
                : ToEnum(targetType, ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder));
        }

        var typeCode = Type.GetTypeCode(targetType);
        RequireIntegralRange(propertyPath, dataType, targetType);
        return (raw, _) => ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue)
            ? null
            : BoxIntegral(ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder), typeCode);
    }

    private static object BoxIntegral(Int128 value, TypeCode typeCode) => typeCode switch
    {
        TypeCode.Byte => (byte)value,
        TypeCode.SByte => (sbyte)value,
        TypeCode.Int16 => (short)value,
        TypeCode.UInt16 => (ushort)value,
        TypeCode.Int32 => (int)value,
        TypeCode.UInt32 => (uint)value,
        TypeCode.Int64 => (long)value,
        TypeCode.UInt64 => (ulong)value,
        _ => throw new ArgumentOutOfRangeException(nameof(typeCode), typeCode, null)
    };

    // Enum.ToObject has no Int128 overload; a negative value only occurs for a signed underlying type.
    private static object ToEnum(Type enumType, Int128 value)
        => value < Int128.Zero ? Enum.ToObject(enumType, (long)value) : Enum.ToObject(enumType, (ulong)value);

    private static void RequireIntegralRange(string propertyPath, ModbusDataType dataType, Type targetType)
    {
        if (GetDataTypeRange(dataType) is not { } dataRange ||
            GetIntegralTypeRange(targetType) is not { } targetRange)
        {
            throw ModbusConfigurationException.ForMapping(propertyPath, $"{dataType} cannot be converted to {targetType.Name}.");
        }

        if (dataRange.Minimum < targetRange.Minimum || dataRange.Maximum > targetRange.Maximum)
        {
            throw ModbusConfigurationException.ForMapping(propertyPath, $"{targetType.Name} cannot hold every {dataType} value.");
        }
    }

    private static (decimal Minimum, decimal Maximum)? GetDataTypeRange(ModbusDataType dataType) => dataType switch
    {
        ModbusDataType.U16 => (0m, ushort.MaxValue),
        ModbusDataType.S16 => (short.MinValue, short.MaxValue),
        ModbusDataType.U32 => (0m, uint.MaxValue),
        ModbusDataType.S32 => (int.MinValue, int.MaxValue),
        ModbusDataType.U64 => (0m, ulong.MaxValue),
        ModbusDataType.S64 => (long.MinValue, long.MaxValue),
        _ => null
    };

    private static (decimal Minimum, decimal Maximum)? GetIntegralTypeRange(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Byte => (byte.MinValue, byte.MaxValue),
        TypeCode.SByte => (sbyte.MinValue, sbyte.MaxValue),
        TypeCode.Int16 => (short.MinValue, short.MaxValue),
        TypeCode.UInt16 => (ushort.MinValue, ushort.MaxValue),
        TypeCode.Int32 => (int.MinValue, int.MaxValue),
        TypeCode.UInt32 => (uint.MinValue, uint.MaxValue),
        TypeCode.Int64 => (long.MinValue, long.MaxValue),
        TypeCode.UInt64 => (ulong.MinValue, ulong.MaxValue),
        _ => null
    };

    // Divides for negative exponents: 10^-n is not exact in binary, so 3 * 0.1 would give 0.30000000000000004.
    private static double ApplyDoubleScale(double value, bool hasDynamicScale, double staticScale, int exponent)
    {
        if (!hasDynamicScale)
        {
            return value * staticScale;
        }

        var scaled = exponent < 0 ? value / Math.Pow(10, -exponent) : value * Math.Pow(10, exponent);
        return staticScale is 1.0 ? scaled : scaled * staticScale;
    }

    private static decimal GetDecimalScale(bool hasDynamicScale, decimal staticScale, int exponent)
    {
        if (!hasDynamicScale)
        {
            return staticScale;
        }

        if (exponent is < -MaximumDecimalExponent or > MaximumDecimalExponent)
        {
            throw new OverflowException($"Scale factor exponent {exponent} is outside the decimal range.");
        }

        var power = exponent < 0 ? NegativePowersOfTen[-exponent] : PowersOfTen[exponent];
        return staticScale == 1m ? power : staticScale * power;
    }

    private static decimal ToDecimalScale(string propertyPath, double scale)
    {
        try
        {
            return (decimal)scale;
        }
        catch (OverflowException)
        {
            throw ModbusConfigurationException.ForMapping(propertyPath, "Scale is outside the decimal range.");
        }
    }

    private static decimal[] CreatePowersOfTen()
    {
        var powers = new decimal[MaximumDecimalExponent + 1];
        powers[0] = 1m;
        for (var exponent = 1; exponent <= MaximumDecimalExponent; exponent++)
        {
            powers[exponent] = powers[exponent - 1] * 10m;
        }

        return powers;
    }

    private static decimal[] CreateNegativePowersOfTen()
    {
        var powers = new decimal[MaximumDecimalExponent + 1];
        for (var exponent = 0; exponent <= MaximumDecimalExponent; exponent++)
        {
            powers[exponent] = new decimal(1, 0, 0, false, (byte)exponent);
        }

        return powers;
    }

    private static void RequireTarget(string propertyPath, bool condition, ModbusDataType dataType, Type targetType)
    {
        if (!condition)
        {
            throw ModbusConfigurationException.ForMapping(propertyPath, $"{dataType} cannot be converted to {targetType.Name}.");
        }
    }

    private static void RequireUnscaled(string propertyPath, bool isScaled, Type targetType)
    {
        if (isScaled)
        {
            throw ModbusConfigurationException.ForMapping(propertyPath, $"Scaling requires a float, double or decimal property, not {targetType.Name}.");
        }
    }
}
