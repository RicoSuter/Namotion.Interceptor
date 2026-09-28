using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikModelTests
{
    [Fact]
    public void WhenCompressorFlagIsSet_ThenCompressorIsRunning()
    {
        // Arrange
        var status = new LuxtronikOperatingStatus();

        // Act
        status.HeatPumpStatus = LuxtronikHeatPumpStatus.Compressor2;

        // Assert
        Assert.True(status.IsCompressorRunning);
        Assert.False(status.IsAuxiliaryHeaterRunning);
    }

    [Fact]
    public void WhenHeatPumpStatusIsUnknown_ThenRunningFlagsAreNull()
    {
        // Act
        var status = new LuxtronikOperatingStatus();

        // Assert
        Assert.Null(status.IsCompressorRunning);
        Assert.Null(status.IsAuxiliaryHeaterRunning);
    }

    [Theory]
    [InlineData(true, false, LuxtronikSmartGridState.Locked)]
    [InlineData(false, false, LuxtronikSmartGridState.Reduced)]
    [InlineData(false, true, LuxtronikSmartGridState.Normal)]
    [InlineData(true, true, LuxtronikSmartGridState.Increased)]
    public void WhenEvuSignalsAreSet_ThenSmartGridStateFollowsTheManual(bool evu1, bool evu2, LuxtronikSmartGridState expected)
    {
        // Arrange
        var smartGrid = new LuxtronikSmartGrid();

        // Act
        smartGrid.Evu1 = evu1;
        smartGrid.Evu2 = evu2;

        // Assert
        Assert.Equal(expected, smartGrid.State);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, null)]
    [InlineData(null, false)]
    public void WhenAnEvuSignalIsUnknown_ThenSmartGridStateIsNull(bool? evu1, bool? evu2)
    {
        // Arrange
        var smartGrid = new LuxtronikSmartGrid();

        // Act
        smartGrid.Evu1 = evu1;
        smartGrid.Evu2 = evu2;

        // Assert
        Assert.Null(smartGrid.State);
    }

    [Fact]
    public void WhenTemperaturesAreConstructed_ThenSensorsUseTheirRegisterAddresses()
    {
        // Act
        var temperatures = new LuxtronikTemperatures();

        // Assert
        Assert.Equal(10108, temperatures.Outside.BaseAddress);
        Assert.Equal(10120, temperatures.HotWater.BaseAddress);
        Assert.Null(((ILuxtronikGatedSubject)temperatures.Outside).MinimumFirmwareVersion);
        Assert.Equal(new Version(3, 92, 0), ((ILuxtronikGatedSubject)temperatures.HeatSourceInlet).MinimumFirmwareVersion);
        Assert.Equal("Outside temperature", temperatures.Outside.Title);
    }

    [Fact]
    public void WhenInspectingRegisterProperties_ThenEveryOneHasStateWithUnitOrDiscreteFlag()
    {
        // Arrange
        var registerProperties = GetRegisterProperties();

        // Act
        var invalidProperties = registerProperties
            .Where(property => !HasValidState(property))
            .Select(GetDisplayName)
            .ToList();

        // Assert
        Assert.NotEmpty(registerProperties);
        Assert.Empty(invalidProperties);
    }

    [Fact]
    public void WhenInspectingCoolingPoolAndSolarRegisters_ThenEachRequiresItsFeature()
    {
        // Arrange
        var featuresByPrefix = new Dictionary<string, LuxtronikFeature>
        {
            ["Cooling"] = LuxtronikFeature.Cooling,
            ["Pool"] = LuxtronikFeature.Pool,
            ["Solar"] = LuxtronikFeature.Solar
        };

        // Act
        var gatedRegisters = GetRegisterProperties()
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<LuxtronikRegisterAttribute>()))
            .Where(register => register.Attribute is not null)
            .Select(register => (
                register.Property,
                register.Attribute!.Feature,
                ExpectedFeature: featuresByPrefix
                    .Where(pair => register.Property.Name.StartsWith(pair.Key, StringComparison.Ordinal))
                    .Select(pair => (LuxtronikFeature?)pair.Value)
                    .FirstOrDefault()))
            .Where(register => register.ExpectedFeature is not null)
            .ToList();

        var ungatedRegisters = gatedRegisters
            .Where(register => register.Feature != register.ExpectedFeature)
            .Select(register => GetDisplayName(register.Property))
            .ToList();

        // Assert
        Assert.NotEmpty(gatedRegisters);
        Assert.Empty(ungatedRegisters);
    }

    private static List<PropertyInfo> GetRegisterProperties()
        => typeof(LuxtronikRegisterAttribute).Assembly.GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(property => property.GetCustomAttribute<ModbusRegisterAttribute>() is not null)
            .ToList();

    private static bool HasValidState(PropertyInfo property)
    {
        if (property.GetCustomAttribute<StateAttribute>() is not { } state)
        {
            return false;
        }

        var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (valueType == typeof(bool) || valueType.IsEnum)
        {
            return state.IsDiscrete && state.Unit == StateUnit.Default;
        }

        if (state.Unit is StateUnit.WattHour or StateUnit.Hour && !state.IsCumulative)
        {
            return false;
        }

        return state.IsDiscrete || state.Unit != StateUnit.Default;
    }

    private static string GetDisplayName(PropertyInfo property)
        => $"{property.DeclaringType?.Name}.{property.Name}";
}
