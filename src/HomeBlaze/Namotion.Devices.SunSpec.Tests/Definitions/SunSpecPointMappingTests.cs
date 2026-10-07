using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec.Tests.Definitions;

public class SunSpecPointMappingTests
{
    private static SunSpecPointDefinition Point(
        string type, int size = 1, string? units = null, SunSpecScaleFactor scaleFactor = default, string? label = null,
        IReadOnlyList<SunSpecSymbolDefinition>? symbols = null, string? access = null)
        => new()
        {
            Name = "P",
            Type = type,
            Size = size,
            Units = units,
            Label = label,
            ScaleFactor = scaleFactor,
            Symbols = symbols ?? [],
            Access = access
        };

    [Fact]
    public void WhenEnumerationHasSymbols_ThenItIsAnEnumeration()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("enum16", symbols: [new SunSpecSymbolDefinition { Name = "OFF", Value = 1 }]))!;

        // Assert
        Assert.Equal(SunSpecValueKind.Enumeration, map.Kind);
        Assert.Equal(typeof(ushort?), map.PropertyType);
        Assert.Equal(ModbusNotAvailableValue.UnsignedMaximum, map.NotAvailableValue);
    }

    [Fact]
    public void WhenPointIsScaleFactor_ThenItIsAScaleFactorWithoutUnit()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("sunssf", units: "SF", label: "W_SF"))!;

        // Assert
        Assert.Equal(SunSpecValueKind.ScaleFactor, map.Kind);
        Assert.Equal(ModbusDataType.S16, map.DataType);
        Assert.Equal(typeof(short?), map.PropertyType);
        Assert.Equal(StateUnit.Default, map.Unit);
        Assert.Equal("W_SF", map.Title);
    }

    [Fact]
    public void WhenPointIsFloat_ThenItIsANumber()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("float32", size: 2, units: "V"))!;

        // Assert
        Assert.Equal(ModbusDataType.F32, map.DataType);
        Assert.Equal(SunSpecValueKind.Number, map.Kind);
        Assert.Equal(StateUnit.Volt, map.Unit);
        Assert.Equal(ModbusNotAvailableValue.None, map.NotAvailableValue);
    }

    [Fact]
    public void WhenPointIsReadWrite_ThenItIsNotReadOnly()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", access: "RW"))!;

        // Assert
        Assert.False(map.IsReadOnly);
    }

    [Fact]
    public void WhenPointHasNoLabel_ThenTitleIsTheName()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", label: "  "))!;

        // Assert
        Assert.Equal("P", map.Title);
    }

    [Fact]
    public void WhenUnitIsPercentSuffixForm_ThenReferenceIsAppendedToTheTitle()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", units: "VNomPct", label: "Voltage"))!;

        // Assert
        Assert.Equal(StateUnit.Percent, map.Unit);
        Assert.Equal(0.01m, map.Scale);
        Assert.Equal("Voltage [of VNom]", map.Title);
    }

    [Fact]
    public void WhenPointTypeIsUnknown_ThenMappingFails()
    {
        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecPointMapping.TryMap(Point("uint128")));
        Assert.Contains("Point P has the unknown type uint128", exception.Message);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(-29)]
    public void WhenFixedScaleFactorIsOutOfRange_ThenMappingFails(int exponent)
    {
        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecPointMapping.TryMap(Point("uint16", units: "V", scaleFactor: new(null, exponent))));
        Assert.Contains("Point P ", exception.Message);
    }

    [Fact]
    public void WhenPointIsScaledPower_ThenItIsADecimalWattValueWithScaleFactor()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("int16", units: "W", scaleFactor: new("W_SF", null), label: "Watts"))!;

        // Assert
        Assert.Equal(ModbusDataType.S16, map.DataType);
        Assert.Equal(SunSpecValueKind.Number, map.Kind);
        Assert.Equal(StateUnit.Watt, map.Unit);
        Assert.Equal(1m, map.Scale);
        Assert.Equal("W_SF", map.ScaleFactorPointName);
        Assert.Equal(ModbusNotAvailableValue.SignedMinimum, map.NotAvailableValue);
        Assert.Equal("Watts", map.Title);
        Assert.True(map.IsReadOnly);
    }

    [Fact]
    public void WhenPointIsPercent_ThenItIsAFractionInPercent()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", units: "Pct", scaleFactor: new("Pct_SF", null), label: "State of Charge"))!;

        // Assert
        Assert.Equal(StateUnit.Percent, map.Unit);
        Assert.Equal(0.01m, map.Scale);
        Assert.Equal("State of Charge", map.Title);
    }

    [Fact]
    public void WhenPointIsPercentOfReference_ThenReferenceIsAppendedToTheTitle()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", units: "% WMax", label: "Power Limit"))!;

        // Assert
        Assert.Equal(StateUnit.Percent, map.Unit);
        Assert.Equal(0.01m, map.Scale);
        Assert.Equal("Power Limit [of WMax]", map.Title);
    }

    [Theory]
    [InlineData("% WMax/sec")]
    [InlineData("ohms")]
    public void WhenUnitIsUnknown_ThenUnitIsAppendedToTheTitle(string units)
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", units: units, label: "Value"))!;

        // Assert
        Assert.Equal(StateUnit.Default, map.Unit);
        Assert.Equal(SunSpecValueKind.Number, map.Kind);
        Assert.Equal($"Value [{units}]", map.Title);
    }

    [Fact]
    public void WhenPointIsMilliseconds_ThenItIsADurationInSeconds()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint32", size: 2, units: "mSecs"))!;

        // Assert
        Assert.Equal(SunSpecValueKind.Duration, map.Kind);
        Assert.Equal(typeof(TimeSpan?), map.PropertyType);
        Assert.Equal(StateUnit.Default, map.Unit);
        Assert.Equal(0.001m, map.Scale);
    }

    [Fact]
    public void WhenPointIsScaledSeconds_ThenItIsADurationWithScaleFactor()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", units: "Secs", scaleFactor: new("Tms_SF", null), label: "Time"))!;

        // Assert
        Assert.Equal(SunSpecValueKind.Duration, map.Kind);
        Assert.Equal(1m, map.Scale);
        Assert.Equal("Tms_SF", map.ScaleFactorPointName);
        Assert.Equal("Time", map.Title);
    }

    [Fact]
    public void WhenPointIsUnscaledSeconds_ThenItIsADuration()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", units: "Secs", label: "Time"))!;

        // Assert
        Assert.Equal(SunSpecValueKind.Duration, map.Kind);
        Assert.Equal(1m, map.Scale);
        Assert.Null(map.ScaleFactorPointName);
        Assert.Equal("Time", map.Title);
    }

    [Theory]
    [InlineData("Tms", 1)]
    [InlineData("Tmh", 3600)]
    [InlineData("Tmd", 86400)]
    public void WhenPointHasVendorTimeUnit_ThenItIsADurationScaledToSeconds(string units, int expectedScale)
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", units: units, label: "Time"))!;

        // Assert
        Assert.Equal(SunSpecValueKind.Duration, map.Kind);
        Assert.Equal(StateUnit.Default, map.Unit);
        Assert.Equal(expectedScale, map.Scale);
        Assert.Equal("Time", map.Title);
    }

    [Fact]
    public void WhenPointHasNumericScaleFactor_ThenItBecomesAStaticScale()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint16", units: "V", scaleFactor: new(null, -2)))!;

        // Assert
        Assert.Equal(0.01m, map.Scale);
        Assert.Null(map.ScaleFactorPointName);
    }

    [Fact]
    public void WhenPointIsAccumulator_ThenItIsCumulativeWithoutNotAvailablePattern()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("acc32", size: 2, units: "Wh", scaleFactor: new("WH_SF", null)))!;

        // Assert
        Assert.Equal(ModbusDataType.U32, map.DataType);
        Assert.True(map.IsCumulative);
        Assert.Equal(ModbusNotAvailableValue.None, map.NotAvailableValue);
        Assert.Equal(StateUnit.WattHour, map.Unit);
    }

    [Fact]
    public void WhenPointIsUnsigned64Bit_ThenItUsesU64()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("uint64", size: 4, units: "Wh", scaleFactor: new("TotWh_SF", null)))!;

        // Assert
        Assert.Equal(ModbusDataType.U64, map.DataType);
        Assert.Equal(ModbusNotAvailableValue.UnsignedMaximum, map.NotAvailableValue);
    }

    [Fact]
    public void WhenEnumerationHasNoSymbols_ThenItIsARawInteger()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("enum16"))!;

        // Assert
        Assert.Equal(SunSpecValueKind.Integer, map.Kind);
        Assert.Equal(typeof(ushort?), map.PropertyType);
    }

    [Fact]
    public void WhenPointIsString_ThenItIsTextWithItsLength()
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point("string", size: 16))!;

        // Assert
        Assert.Equal(ModbusDataType.String, map.DataType);
        Assert.Equal(16, map.StringLength);
        Assert.Equal(typeof(string), map.PropertyType);
    }

    [Theory]
    [InlineData("pad", 1)]
    [InlineData("ipv6addr", 8)]
    [InlineData("string", 126)]
    public void WhenPointHasNoValueMapping_ThenItIsSkipped(string type, int size)
    {
        // Act
        var map = SunSpecPointMapping.TryMap(Point(type, size));

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenSizeDoesNotMatchType_ThenMappingFails()
    {
        // Act & Assert
        Assert.Throws<InvalidDataException>(() => SunSpecPointMapping.TryMap(Point("uint32", size: 1)));
    }

    [Fact]
    public void WhenMappingEveryBuiltInPoint_ThenOnlyPointsWithoutValueMappingAreSkipped()
    {
        // Arrange
        var points = SunSpecDefinitions.GetBuiltInModelIds()
            .SelectMany(modelId => GetPoints(SunSpecDefinitions.TryGetBuiltIn(modelId)!.Group))
            .ToList();

        // Act
        var skippedTypes = points
            .Where(point => SunSpecPointMapping.TryMap(point) is null)
            .Select(point => point.Type)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.NotEmpty(points);
        Assert.Equal(["ipv6addr", "pad", "string"], skippedTypes);

        static IEnumerable<SunSpecPointDefinition> GetPoints(SunSpecGroupDefinition group)
            => group.Points.Concat(group.Groups.SelectMany(GetPoints));
    }
}
