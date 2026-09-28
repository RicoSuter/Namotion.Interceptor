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

    public static ModbusValueReader Create(ModbusRegisterAttribute attribute, Type propertyType, string propertyPath)
    {
        var underlyingType = Nullable.GetUnderlyingType(propertyType);
        var targetType = underlyingType ?? propertyType;
        var isNullable = underlyingType is not null || !propertyType.IsValueType;

        var dataType = attribute.DataType;
        var wordOrder = attribute.WordOrder;
        var notAvailableValue = attribute.NotAvailableValue;
        var hasDynamicScale = attribute.ScaleFactorProperty is not null;
        var isScaled = hasDynamicScale || attribute.Scale != 1.0;

        if (notAvailableValue != ModbusNotAvailableValue.None)
        {
            if (!isNullable)
            {
                throw Error(propertyPath, "NotAvailableValue requires a nullable property type.");
            }

            if (dataType is ModbusDataType.Boolean or ModbusDataType.F32 or ModbusDataType.String)
            {
                throw Error(propertyPath, $"NotAvailableValue is not supported for {dataType}.");
            }
        }

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
                return CreateFloatReader(attribute, targetType, propertyPath, wordOrder, isNullable, hasDynamicScale, isScaled);

            default:
                return CreateIntegerReader(attribute, targetType, propertyPath, dataType, wordOrder, notAvailableValue, hasDynamicScale, isScaled);
        }
    }

    private static ModbusValueReader CreateFloatReader(
        ModbusRegisterAttribute attribute, Type targetType, string propertyPath,
        ModbusWordOrder wordOrder, bool isNullable, bool hasDynamicScale, bool isScaled)
    {
        var staticScale = attribute.Scale;
        if (targetType == typeof(float))
        {
            return (raw, exponent) =>
            {
                var value = ModbusRegisterCodec.ReadSingle(raw, wordOrder);
                return isScaled ? (float)(value * GetDoubleScale(hasDynamicScale, staticScale, exponent)) : value;
            };
        }

        if (targetType == typeof(double))
        {
            return (raw, exponent) => ModbusRegisterCodec.ReadSingle(raw, wordOrder) * GetDoubleScale(hasDynamicScale, staticScale, exponent);
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

        throw Error(propertyPath, $"F32 requires a float, double or decimal property, not {targetType.Name}.");
    }

    private static ModbusValueReader CreateIntegerReader(
        ModbusRegisterAttribute attribute, Type targetType, string propertyPath, ModbusDataType dataType,
        ModbusWordOrder wordOrder, ModbusNotAvailableValue notAvailableValue, bool hasDynamicScale, bool isScaled)
    {
        var staticScale = attribute.Scale;

        if (targetType == typeof(decimal))
        {
            var decimalScale = ToDecimalScale(propertyPath, staticScale);
            return (raw, exponent) => IsNotAvailable(raw)
                ? null
                : ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) * GetDecimalScale(hasDynamicScale, decimalScale, exponent);
        }

        if (targetType == typeof(double))
        {
            return (raw, exponent) => IsNotAvailable(raw)
                ? null
                : ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) * GetDoubleScale(hasDynamicScale, staticScale, exponent);
        }

        if (targetType == typeof(float))
        {
            return (raw, exponent) => IsNotAvailable(raw)
                ? null
                : (float)(ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) * GetDoubleScale(hasDynamicScale, staticScale, exponent));
        }

        RequireUnscaled(propertyPath, isScaled, targetType);

        if (targetType == typeof(bool))
        {
            return (raw, _) => IsNotAvailable(raw)
                ? null
                : ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) != 0 ? True : False;
        }

        if (targetType.IsEnum)
        {
            RequireIntegralRange(propertyPath, dataType, Enum.GetUnderlyingType(targetType));
            return (raw, _) => IsNotAvailable(raw)
                ? null
                : Enum.ToObject(targetType, ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder));
        }

        var typeCode = Type.GetTypeCode(targetType);
        RequireIntegralRange(propertyPath, dataType, targetType);
        return (raw, _) => IsNotAvailable(raw)
            ? null
            : BoxIntegral(ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder), typeCode);

        bool IsNotAvailable(ReadOnlySpan<byte> raw)
            => ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue);
    }

    private static object BoxIntegral(long value, TypeCode typeCode) => typeCode switch
    {
        TypeCode.Byte => (byte)value,
        TypeCode.SByte => (sbyte)value,
        TypeCode.Int16 => (short)value,
        TypeCode.UInt16 => (ushort)value,
        TypeCode.Int32 => (int)value,
        TypeCode.UInt32 => (uint)value,
        TypeCode.Int64 => value,
        TypeCode.UInt64 => (ulong)value,
        _ => throw new ArgumentOutOfRangeException(nameof(typeCode), typeCode, null)
    };

    private static void RequireIntegralRange(string propertyPath, ModbusDataType dataType, Type targetType)
    {
        var (dataMinimum, dataMaximum) = dataType switch
        {
            ModbusDataType.U16 => (0m, (decimal)ushort.MaxValue),
            ModbusDataType.S16 => ((decimal)short.MinValue, (decimal)short.MaxValue),
            ModbusDataType.U32 => (0m, (decimal)uint.MaxValue),
            ModbusDataType.S32 => ((decimal)int.MinValue, (decimal)int.MaxValue),
            _ => throw Error(propertyPath, $"{dataType} cannot be converted to {targetType.Name}.")
        };

        var (targetMinimum, targetMaximum) = Type.GetTypeCode(targetType) switch
        {
            TypeCode.Byte => ((decimal)byte.MinValue, (decimal)byte.MaxValue),
            TypeCode.SByte => ((decimal)sbyte.MinValue, (decimal)sbyte.MaxValue),
            TypeCode.Int16 => ((decimal)short.MinValue, (decimal)short.MaxValue),
            TypeCode.UInt16 => ((decimal)ushort.MinValue, (decimal)ushort.MaxValue),
            TypeCode.Int32 => ((decimal)int.MinValue, (decimal)int.MaxValue),
            TypeCode.UInt32 => ((decimal)uint.MinValue, (decimal)uint.MaxValue),
            TypeCode.Int64 => ((decimal)long.MinValue, (decimal)long.MaxValue),
            TypeCode.UInt64 => ((decimal)ulong.MinValue, (decimal)ulong.MaxValue),
            _ => throw Error(propertyPath, $"{dataType} cannot be converted to {targetType.Name}.")
        };

        if (dataMinimum < targetMinimum || dataMaximum > targetMaximum)
        {
            throw Error(propertyPath, $"{targetType.Name} cannot hold every {dataType} value.");
        }
    }

    private static double GetDoubleScale(bool hasDynamicScale, double staticScale, int exponent)
        => hasDynamicScale ? Math.Pow(10, exponent) : staticScale;

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

        return exponent < 0 ? NegativePowersOfTen[-exponent] : PowersOfTen[exponent];
    }

    private static decimal ToDecimalScale(string propertyPath, double scale)
    {
        try
        {
            return (decimal)scale;
        }
        catch (OverflowException)
        {
            throw Error(propertyPath, "Scale is outside the decimal range.");
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
            throw Error(propertyPath, $"{dataType} cannot be converted to {targetType.Name}.");
        }
    }

    private static void RequireUnscaled(string propertyPath, bool isScaled, Type targetType)
    {
        if (isScaled)
        {
            throw Error(propertyPath, $"Scaling requires a float, double or decimal property, not {targetType.Name}.");
        }
    }

    private static ModbusConfigurationException Error(string propertyPath, string message)
        => new($"Invalid Modbus mapping on {propertyPath}: {message}");
}
