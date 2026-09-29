using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Devices.Luxtronik.Model;
using Namotion.Interceptor.Modbus;
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
    public void WhenTemperaturesAreConstructed_ThenRoomTemperatureRequiresTheRoomControlUnit()
    {
        // Act
        var temperatures = new LuxtronikTemperatures();

        // Assert
        Assert.Equal(10106, temperatures.Room.BaseAddress);
        Assert.Equal(LuxtronikFeature.RoomControlUnit, ((ILuxtronikGatedSubject)temperatures.Room).Feature);
        Assert.Equal(LuxtronikFeature.None, ((ILuxtronikGatedSubject)temperatures.Outside).Feature);
    }

    [Theory]
    [InlineData(1, 10140, 10141, 10010, 10015, LuxtronikFeature.MixingCircuit1Heating, LuxtronikFeature.MixingCircuit1Cooling)]
    [InlineData(2, 10150, 10151, 10020, 10025, LuxtronikFeature.MixingCircuit2Heating, LuxtronikFeature.MixingCircuit2Cooling)]
    [InlineData(3, 10160, 10161, 10030, 10035, LuxtronikFeature.MixingCircuit3Heating, LuxtronikFeature.MixingCircuit3Cooling)]
    public void WhenMixingCircuitIsConstructed_ThenChildrenUseItsAddressesAndFeatures(
        int index, int temperatureAddress, int setpointsAddress, int heatingAddress, int coolingAddress,
        LuxtronikFeature heatingFeature, LuxtronikFeature coolingFeature)
    {
        // Act
        var circuit = new LuxtronikMixingCircuit(index);

        // Assert
        Assert.Equal(temperatureAddress, circuit.Temperature.BaseAddress);
        Assert.Equal(setpointsAddress, circuit.Setpoints.BaseAddress);
        Assert.Equal(heatingAddress, circuit.Heating.BaseAddress);
        Assert.Equal(coolingAddress, circuit.Cooling.BaseAddress);
        Assert.Equal(heatingFeature, ((ILuxtronikGatedSubject)circuit.Temperature).Feature);
        Assert.Equal(heatingFeature, ((ILuxtronikGatedSubject)circuit.Setpoints).Feature);
        Assert.Equal(heatingFeature, ((ILuxtronikGatedSubject)circuit.Heating).Feature);
        Assert.Equal(coolingFeature, ((ILuxtronikGatedSubject)circuit.Cooling).Feature);
        Assert.Equal($"Mixing circuit {index}", circuit.Title);
        Assert.Equal($"Mixing circuit {index} temperature", circuit.Temperature.Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void WhenMixingCircuitIndexIsOutOfRange_ThenArgumentOutOfRangeExceptionIsThrown(int index)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuxtronikMixingCircuit(index));
    }

    [Fact]
    public void WhenPumpIsConstructed_ThenItIsASwitchStateAtItsRegisterFromFirmware392()
    {
        // Act
        var pump = new LuxtronikPump(10354, "Heating circulation pump (HUP)");

        // Assert
        Assert.IsAssignableFrom<ISwitchState>(pump);
        Assert.Equal(10354, pump.BaseAddress);
        Assert.Equal("Heating circulation pump (HUP)", pump.Title);
        Assert.Null(pump.IsOn);
        Assert.Equal(LuxtronikGating.Firmware392, ((ILuxtronikGatedSubject)pump).MinimumFirmwareVersion);
    }

    [Fact]
    public void WhenHoldingSubjectsAreConstructed_ThenTheirGatesFollowTheManual()
    {
        // Act
        var roomControl = (ILuxtronikGatedSubject)new LuxtronikRoomControl();
        var overallHeating = (ILuxtronikGatedSubject)new LuxtronikOverallSmartHomeControl();
        var hotWaterRequests = (ILuxtronikGatedSubject)new LuxtronikHotWaterRequests();
        var control = (ILuxtronikGatedSubject)new LuxtronikSmartHomeControl(10000, LuxtronikFeature.None);

        // Assert
        Assert.Equal(new Version(3, 92, 1), roomControl.MinimumFirmwareVersion);
        Assert.Equal(LuxtronikFeature.RoomControlUnit, roomControl.Feature);
        Assert.Equal(LuxtronikGating.Firmware392, overallHeating.MinimumFirmwareVersion);
        Assert.Equal(LuxtronikFeature.None, overallHeating.Feature);
        Assert.Equal(LuxtronikGating.Firmware392, hotWaterRequests.MinimumFirmwareVersion);
        Assert.Equal(LuxtronikFeature.None, hotWaterRequests.Feature);
        Assert.Null(control.MinimumFirmwareVersion);
    }

    [Theory]
    [InlineData(typeof(LuxtronikLocks), nameof(LuxtronikLocks.Heating), "3.92.0", LuxtronikFeature.None)]
    [InlineData(typeof(LuxtronikLocks), nameof(LuxtronikLocks.HotWater), "3.92.0", LuxtronikFeature.None)]
    [InlineData(typeof(LuxtronikLocks), nameof(LuxtronikLocks.Cooling), null, LuxtronikFeature.Cooling)]
    [InlineData(typeof(LuxtronikLocks), nameof(LuxtronikLocks.Pool), null, LuxtronikFeature.Pool)]
    [InlineData(typeof(LuxtronikSmartHomeControl), nameof(LuxtronikSmartHomeControl.Level), "3.92.0", LuxtronikFeature.None)]
    public void WhenInspectingHoldingRegisters_ThenRegisterGatesFollowTheManual(
        Type subjectType, string propertyName, string? minimumFirmware, LuxtronikFeature feature)
    {
        // Act
        var attribute = subjectType.GetProperty(propertyName)!.GetCustomAttribute<LuxtronikHoldingRegisterAttribute>()!;

        // Assert
        Assert.Equal(minimumFirmware, attribute.MinimumFirmware);
        Assert.Equal(feature, attribute.Feature);
    }

    [Fact]
    public void WhenInspectingRegisterProperties_ThenAllAreReadOnly()
    {
        // Arrange
        var registerProperties = GetRegisterProperties();

        // Act
        var writableHoldingRegisters = registerProperties
            .Where(property => property.GetCustomAttribute<LuxtronikRegisterAttribute>() is
                { Space: ModbusAddressSpace.HoldingRegister, Access: not ModbusAccess.ReadOnly })
            .Select(GetDisplayName)
            .ToList();

        var publiclySettableProperties = registerProperties
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(GetDisplayName)
            .ToList();

        // Assert
        Assert.Contains(registerProperties, property =>
            property.GetCustomAttribute<LuxtronikRegisterAttribute>()?.Space == ModbusAddressSpace.HoldingRegister);
        Assert.Empty(writableHoldingRegisters);
        Assert.Empty(publiclySettableProperties);
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
