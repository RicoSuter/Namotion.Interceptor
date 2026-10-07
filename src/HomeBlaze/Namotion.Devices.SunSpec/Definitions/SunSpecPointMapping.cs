using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec.Definitions;

/// <summary>
/// How a SunSpec point value is represented as a property.
/// </summary>
internal enum SunSpecValueKind
{
    /// <summary>A measurement as <see cref="decimal"/>, scaled and converted to its unit.</summary>
    Number,

    /// <summary>A time as <see cref="TimeSpan"/>, scaled to seconds.</summary>
    Duration,

    /// <summary>A raw integer without unit or scaling.</summary>
    Integer,

    /// <summary>A power-of-ten exponent other points refer to.</summary>
    ScaleFactor,

    /// <summary>An enumeration with named values.</summary>
    Enumeration,

    /// <summary>A bit field with named bits.</summary>
    Flags,

    /// <summary>ASCII text.</summary>
    Text
}

/// <summary>
/// The register mapping and property shape of one SunSpec point.
/// </summary>
internal sealed class SunSpecPointMap
{
    /// <summary>Gets the Modbus register type.</summary>
    public required ModbusDataType DataType { get; init; }

    /// <summary>Gets the register count of a string, otherwise 0.</summary>
    public required int StringLength { get; init; }

    /// <summary>Gets how the value is represented as a property.</summary>
    public required SunSpecValueKind Kind { get; init; }

    /// <summary>Gets the integer type of the raw value, or <see cref="float"/> and <see cref="string"/>.</summary>
    public required Type RawType { get; init; }

    /// <summary>Gets the SunSpec "not implemented" pattern.</summary>
    public required ModbusNotAvailableValue NotAvailableValue { get; init; }

    /// <summary>Gets the HomeBlaze unit of a number.</summary>
    public required StateUnit Unit { get; init; }

    /// <summary>Gets the static factor of a number: unit conversion times a fixed power of ten from the definition.</summary>
    public required decimal Scale { get; init; }

    /// <summary>Gets the <c>sunssf</c> point a number is scaled by, or <c>null</c>.</summary>
    public required string? ScaleFactorPointName { get; init; }

    /// <summary>Gets whether the point is an accumulator.</summary>
    public required bool IsCumulative { get; init; }

    /// <summary>Gets whether the point is read only (its access is not "RW").</summary>
    public required bool IsReadOnly { get; init; }

    /// <summary>Gets the display title, with an unmapped unit appended.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the nullable property type of a dynamic model: <see cref="decimal"/> for numbers, <see cref="TimeSpan"/>
    /// for durations, <see cref="string"/> for text, otherwise the raw integer type.
    /// </summary>
    public Type PropertyType => Kind switch
    {
        SunSpecValueKind.Number => typeof(decimal?),
        SunSpecValueKind.Duration => typeof(TimeSpan?),
        SunSpecValueKind.Text => typeof(string),
        _ => Type.GetTypeCode(RawType) switch
        {
            TypeCode.Int16 => typeof(short?),
            TypeCode.UInt16 => typeof(ushort?),
            TypeCode.Int32 => typeof(int?),
            TypeCode.UInt32 => typeof(uint?),
            TypeCode.Int64 => typeof(long?),
            _ => typeof(ulong?)
        }
    };
}

/// <summary>
/// A point that becomes a property, with its register offset within its group.
/// </summary>
internal readonly record struct SunSpecPropertyPoint(SunSpecPointDefinition Point, int Offset, SunSpecPointMap Map);

/// <summary>
/// Maps SunSpec point definitions to Modbus registers and properties, shared by the generator and the dynamic models.
/// </summary>
internal static class SunSpecPointMapping
{
    // A string is read in one request, which reads at most 125 registers.
    private const int MaximumStringLength = 125;

    private readonly record struct PointType(
        ModbusDataType DataType, Type RawType, ModbusNotAvailableValue NotAvailableValue, bool IsAccumulator, SunSpecValueKind? Kind);

    private readonly record struct UnitMapping(StateUnit Unit, decimal Factor, string? TitleSuffix, bool IsDuration);

    private readonly record struct Measurement(StateUnit Unit, decimal Scale, string? ScaleFactorPointName, string? TitleSuffix);

    private static readonly UnitMapping NoUnit = new(StateUnit.Default, 1m, null, false);

    private static readonly Measurement NoMeasurement = new(StateUnit.Default, 1m, null, null);

    private static readonly Dictionary<string, PointType> PointTypes = new(StringComparer.Ordinal)
    {
        ["int16"] = new(ModbusDataType.S16, typeof(short), ModbusNotAvailableValue.SignedMinimum, false, null),
        ["uint16"] = new(ModbusDataType.U16, typeof(ushort), ModbusNotAvailableValue.UnsignedMaximum, false, null),
        ["count"] = new(ModbusDataType.U16, typeof(ushort), ModbusNotAvailableValue.UnsignedMaximum, false, null),
        ["acc16"] = new(ModbusDataType.U16, typeof(ushort), ModbusNotAvailableValue.None, true, null),
        ["int32"] = new(ModbusDataType.S32, typeof(int), ModbusNotAvailableValue.SignedMinimum, false, null),
        ["uint32"] = new(ModbusDataType.U32, typeof(uint), ModbusNotAvailableValue.UnsignedMaximum, false, null),
        ["acc32"] = new(ModbusDataType.U32, typeof(uint), ModbusNotAvailableValue.None, true, null),
        ["int64"] = new(ModbusDataType.S64, typeof(long), ModbusNotAvailableValue.SignedMinimum, false, null),
        ["uint64"] = new(ModbusDataType.U64, typeof(ulong), ModbusNotAvailableValue.UnsignedMaximum, false, null),
        ["acc64"] = new(ModbusDataType.U64, typeof(ulong), ModbusNotAvailableValue.None, true, null),
        ["float32"] = new(ModbusDataType.F32, typeof(float), ModbusNotAvailableValue.None, false, SunSpecValueKind.Number),
        ["sunssf"] = new(ModbusDataType.S16, typeof(short), ModbusNotAvailableValue.SignedMinimum, false, SunSpecValueKind.ScaleFactor),
        ["enum16"] = new(ModbusDataType.U16, typeof(ushort), ModbusNotAvailableValue.UnsignedMaximum, false, SunSpecValueKind.Enumeration),
        ["enum32"] = new(ModbusDataType.U32, typeof(uint), ModbusNotAvailableValue.UnsignedMaximum, false, SunSpecValueKind.Enumeration),
        ["bitfield16"] = new(ModbusDataType.U16, typeof(ushort), ModbusNotAvailableValue.UnsignedMaximum, false, SunSpecValueKind.Flags),
        ["bitfield32"] = new(ModbusDataType.U32, typeof(uint), ModbusNotAvailableValue.UnsignedMaximum, false, SunSpecValueKind.Flags),
        ["string"] = new(ModbusDataType.String, typeof(string), ModbusNotAvailableValue.None, false, SunSpecValueKind.Text),
        ["eui48"] = new(ModbusDataType.U64, typeof(ulong), ModbusNotAvailableValue.None, false, SunSpecValueKind.Integer),
        ["ipaddr"] = new(ModbusDataType.U32, typeof(uint), ModbusNotAvailableValue.None, false, SunSpecValueKind.Integer)
    };

    // HomeBlaze percentages are fractions (0 to 1), SunSpec percentages are 0 to 100. Durations are TimeSpan
    // properties in seconds and carry no unit.
    private static readonly Dictionary<string, UnitMapping> Units = new(StringComparer.Ordinal)
    {
        ["W"] = new(StateUnit.Watt, 1m, null, false),
        ["Wh"] = new(StateUnit.WattHour, 1m, null, false),
        ["WH"] = new(StateUnit.WattHour, 1m, null, false),
        ["kWh"] = new(StateUnit.KilowattHour, 1m, null, false),
        ["VA"] = new(StateUnit.VoltAmpere, 1m, null, false),
        ["var"] = new(StateUnit.VoltAmpereReactive, 1m, null, false),
        ["Var"] = new(StateUnit.VoltAmpereReactive, 1m, null, false),
        ["VAh"] = new(StateUnit.VoltAmpereHour, 1m, null, false),
        ["varh"] = new(StateUnit.VoltAmpereReactiveHour, 1m, null, false),
        ["Varh"] = new(StateUnit.VoltAmpereReactiveHour, 1m, null, false),
        ["A"] = new(StateUnit.Ampere, 1m, null, false),
        ["Ah"] = new(StateUnit.AmpereHour, 1m, null, false),
        ["AH"] = new(StateUnit.AmpereHour, 1m, null, false),
        ["kAH"] = new(StateUnit.AmpereHour, 1000m, null, false),
        ["V"] = new(StateUnit.Volt, 1m, null, false),
        ["Hz"] = new(StateUnit.Hertz, 1m, null, false),
        ["C"] = new(StateUnit.DegreeCelsius, 1m, null, false),
        ["W/m2"] = new(StateUnit.WattPerSquareMeter, 1m, null, false),
        ["W/m^2"] = new(StateUnit.WattPerSquareMeter, 1m, null, false),
        ["Degrees"] = new(StateUnit.Degree, 1m, null, false),
        ["deg"] = new(StateUnit.Degree, 1m, null, false),
        ["HPa"] = new(StateUnit.Hectopascal, 1m, null, false),
        ["m/s"] = new(StateUnit.MeterPerSecond, 1m, null, false),
        ["mps"] = new(StateUnit.MeterPerSecond, 1m, null, false),
        ["meters"] = new(StateUnit.Meter, 1m, null, false),
        ["mm"] = new(StateUnit.Millimeter, 1m, null, false),
        ["Mbps"] = new(StateUnit.MegabitPerSecond, 1m, null, false),
        ["Pct"] = new(StateUnit.Percent, 0.01m, null, false),
        ["%"] = new(StateUnit.Percent, 0.01m, null, false),
        ["Secs"] = new(StateUnit.Default, 1m, null, true),
        ["Sec"] = new(StateUnit.Default, 1m, null, true),
        ["mSecs"] = new(StateUnit.Default, 0.001m, null, true),
        ["Tms"] = new(StateUnit.Default, 1m, null, true),
        ["Tmh"] = new(StateUnit.Default, 3600m, null, true),
        ["Tmd"] = new(StateUnit.Default, 86400m, null, true)
    };

    /// <summary>
    /// Gets the points of <paramref name="group"/> that become properties: the mapped points, except the ID and L points
    /// of a top-level group, which the model's own members cover.
    /// </summary>
    /// <exception cref="InvalidDataException">A point cannot be mapped, see <see cref="TryMap"/>.</exception>
    public static List<SunSpecPropertyPoint> GetPropertyPoints(SunSpecGroupDefinition group, bool isTopLevel)
    {
        var result = new List<SunSpecPropertyPoint>(group.Points.Count);
        var offset = 0;
        for (var index = 0; index < group.Points.Count; index++)
        {
            var point = group.Points[index];
            var pointOffset = offset;
            offset += point.Size;
            if ((!isTopLevel || index >= 2) && TryMap(point) is { } map)
            {
                result.Add(new SunSpecPropertyPoint(point, pointOffset, map));
            }
        }

        return result;
    }

    /// <summary>
    /// Maps a point, or returns <c>null</c> for points without a value mapping: <c>pad</c>, <c>ipv6addr</c>, and strings
    /// longer than one Modbus request can read (125 registers).
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The point type is unknown, its size does not match its type, or its fixed scale factor is outside -28 to 28.
    /// </exception>
    public static SunSpecPointMap? TryMap(SunSpecPointDefinition point)
    {
        if (!PointTypes.TryGetValue(point.Type, out var pointType))
        {
            return point.Type is "pad" or "ipv6addr"
                ? null
                : throw new InvalidDataException($"Point {point.Name} has the unknown type {point.Type}.");
        }

        ValidateSize(point, pointType.DataType);

        if (pointType.DataType == ModbusDataType.String && point.Size > MaximumStringLength)
        {
            return null;
        }

        var unit = MapUnit(point.Units);
        var scale = unit.Factor * (point.ScaleFactor.Exponent is { } exponent ? PowerOfTen(point, exponent) : 1m);
        var kind = GetKind(point, pointType, unit, scale);
        var measurement = kind is SunSpecValueKind.Number or SunSpecValueKind.Duration
            ? new Measurement(unit.Unit, scale, point.ScaleFactor.PointName, unit.TitleSuffix)
            : NoMeasurement;

        return new SunSpecPointMap
        {
            DataType = pointType.DataType,
            StringLength = pointType.DataType == ModbusDataType.String ? point.Size : 0,
            Kind = kind,
            RawType = pointType.RawType,
            NotAvailableValue = pointType.NotAvailableValue,
            Unit = measurement.Unit,
            Scale = measurement.Scale,
            ScaleFactorPointName = measurement.ScaleFactorPointName,
            IsCumulative = pointType.IsAccumulator,
            IsReadOnly = !point.IsWritable,
            Title = GetTitle(point, measurement.TitleSuffix)
        };
    }

    private static void ValidateSize(SunSpecPointDefinition point, ModbusDataType dataType)
    {
        var expectedSize = dataType switch
        {
            ModbusDataType.String => point.Size,
            ModbusDataType.U16 or ModbusDataType.S16 => 1,
            ModbusDataType.U32 or ModbusDataType.S32 or ModbusDataType.F32 => 2,
            _ => 4
        };

        if (point.Size != expectedSize)
        {
            throw new InvalidDataException($"Point {point.Name} of type {point.Type} has size {point.Size}, expected {expectedSize}.");
        }
    }

    private static SunSpecValueKind GetKind(SunSpecPointDefinition point, PointType pointType, UnitMapping unit, decimal scale)
    {
        // An integer with a unit, a scale or an accumulator is a measurement.
        var kind = pointType.Kind ?? (pointType.IsAccumulator || HasMeasurementMeaning(unit) || IsScaled(point, scale)
            ? SunSpecValueKind.Number
            : SunSpecValueKind.Integer);

        if ((kind is SunSpecValueKind.Enumeration or SunSpecValueKind.Flags) && point.Symbols.Count == 0)
        {
            return SunSpecValueKind.Integer;
        }

        return kind == SunSpecValueKind.Number && unit.IsDuration ? SunSpecValueKind.Duration : kind;
    }

    private static bool HasMeasurementMeaning(UnitMapping unit)
        => unit.Unit != StateUnit.Default || unit.TitleSuffix is not null || unit.IsDuration;

    private static bool IsScaled(SunSpecPointDefinition point, decimal scale)
        => point.ScaleFactor.PointName is not null || scale != 1m;

    private static string GetTitle(SunSpecPointDefinition point, string? titleSuffix)
    {
        var label = string.IsNullOrWhiteSpace(point.Label) ? point.Name : point.Label.Trim();
        return titleSuffix is null ? label : $"{label} [{titleSuffix}]";
    }

    private static UnitMapping MapUnit(string? units)
    {
        var trimmed = units?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return NoUnit;
        }

        if (Units.TryGetValue(trimmed, out var mapping))
        {
            return mapping;
        }

        return TryGetPercentReference(trimmed, out var reference)
            ? new UnitMapping(StateUnit.Percent, 0.01m, $"of {reference}", false)
            : NoUnit with { TitleSuffix = trimmed };
    }

    // "% WMax", "%WHRtg" and "VNomPct" are percentages of a reference value; rates such as "% WMax/sec" are not.
    private static bool TryGetPercentReference(string units, out string reference)
    {
        reference = string.Empty;
        if (units.Contains('/'))
        {
            return false;
        }

        if (units.StartsWith('%'))
        {
            reference = units[1..].Trim();
        }
        else if (units.EndsWith("Pct", StringComparison.Ordinal))
        {
            reference = units[..^3].Trim();
        }

        return reference.Length > 0;
    }

    private static decimal PowerOfTen(SunSpecPointDefinition point, int exponent)
    {
        if (exponent is < -28 or > 28)
        {
            throw new InvalidDataException($"Point {point.Name} has the scale factor {exponent}, expected -28 to 28.");
        }

        var result = 1m;
        for (var index = 0; index < Math.Abs(exponent); index++)
        {
            result *= 10m;
        }

        return exponent < 0 ? 1m / result : result;
    }
}
