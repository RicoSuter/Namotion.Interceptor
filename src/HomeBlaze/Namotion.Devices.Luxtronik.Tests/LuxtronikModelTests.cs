using Namotion.Interceptor.Modbus.Client;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Devices.Luxtronik.Model;
using Namotion.Devices.Luxtronik.Tests.Testing;
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
        smartGrid.IsEvu1Active = evu1;
        smartGrid.IsEvu2Active = evu2;

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
        smartGrid.IsEvu1Active = evu1;
        smartGrid.IsEvu2Active = evu2;

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
        Assert.Equal(10105, temperatures.Flow.BaseAddress);
        Assert.Null(((ILuxtronikGate)temperatures.Outside).MinimumFirmwareVersion);
        Assert.Equal(new Version(3, 92, 0), ((ILuxtronikGate)temperatures.HeatSourceInlet).MinimumFirmwareVersion);
        Assert.Equal("Outside temperature", temperatures.Outside.Title);
    }

    [Theory]
    [InlineData(1, 0, 10351, LuxtronikFunction.MixingCircuit1Heating, LuxtronikFunction.MixingCircuit1Cooling)]
    [InlineData(2, 10, 10352, LuxtronikFunction.MixingCircuit2Heating, LuxtronikFunction.MixingCircuit2Cooling)]
    [InlineData(3, 20, 10353, LuxtronikFunction.MixingCircuit3Heating, LuxtronikFunction.MixingCircuit3Cooling)]
    public void WhenMixingCircuitIsConstructed_ThenItAndItsChildrenUseTheCircuitAddressesAndFlags(
        int index, int baseAddress, int pumpAddress, LuxtronikFunction heatingFunction, LuxtronikFunction coolingFunction)
    {
        // Act
        var circuit = new LuxtronikMixingCircuit(index);

        // Assert
        Assert.Equal(baseAddress, circuit.BaseAddress);
        Assert.Equal(10140 + baseAddress, circuit.Temperature.BaseAddress);
        Assert.Equal(pumpAddress, circuit.Pump.BaseAddress);
        Assert.Equal(10010 + baseAddress, circuit.HeatingSmartHomeControl.BaseAddress);
        Assert.Equal(10015 + baseAddress, circuit.CoolingSmartHomeControl.BaseAddress);
        Assert.Equal(heatingFunction, ((ILuxtronikGate)circuit.HeatingSmartHomeControl).Function);
        Assert.Equal(coolingFunction, ((ILuxtronikGate)circuit.CoolingSmartHomeControl).Function);
        Assert.Equal(heatingFunction, LuxtronikFunction.MixingCircuit1Heating + ((ILuxtronikCircuitSubject)circuit).FunctionOffset);
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
        Assert.Equal(LuxtronikGating.Firmware392, ((ILuxtronikGate)pump).MinimumFirmwareVersion);
        Assert.Equal(LuxtronikFunction.None, ((ILuxtronikGate)pump).Function);
    }

    [Fact]
    public void WhenInspectingRegisterProperties_ThenAllAreReadOnly()
    {
        // Arrange
        var registerProperties = GetRegisterProperties();

        // Act
        var writableHoldingRegisters = registerProperties
            .Where(property => property.GetCustomAttribute<LuxtronikRegisterAttribute>() is
                { AddressSpace: ModbusAddressSpace.HoldingRegister, Access: not ModbusAccess.ReadOnly })
            .Select(GetDisplayName)
            .ToList();

        var publiclySettableProperties = registerProperties
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(GetDisplayName)
            .ToList();

        // Assert
        Assert.Contains(registerProperties, property =>
            property.GetCustomAttribute<LuxtronikRegisterAttribute>()?.AddressSpace == ModbusAddressSpace.HoldingRegister);
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
    public void WhenCoolingAndMixingCircuit2CoolingAreActive_ThenOnlyTheirFunctionSubjectsExist()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.UpdateFunctionSubjects(LuxtronikFunctionMask.Of(LuxtronikFunction.Cooling, LuxtronikFunction.MixingCircuit2Cooling));

        // Assert
        Assert.NotNull(heatPump.Cooling);
        Assert.NotNull(heatPump.MixingCircuit2);
        Assert.Null(heatPump.Pool);
        Assert.Null(heatPump.Solar);
        Assert.Null(heatPump.RoomControl);
        Assert.Null(heatPump.MixingCircuit1);
        Assert.Null(heatPump.MixingCircuit3);
    }

    [Fact]
    public void WhenFunctionSubjectsAreUpdatedWithTheSameFunctionsAgain_ThenTheExistingSubjectsAreKept()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        var functionMask = LuxtronikFunctionMask.Of(LuxtronikFunction.Cooling, LuxtronikFunction.MixingCircuit2Cooling);
        heatPump.UpdateFunctionSubjects(functionMask);
        var cooling = heatPump.Cooling;
        var mixingCircuit2 = heatPump.MixingCircuit2;

        // Act
        heatPump.UpdateFunctionSubjects(functionMask);

        // Assert
        Assert.Same(cooling, heatPump.Cooling);
        Assert.Same(mixingCircuit2, heatPump.MixingCircuit2);
    }

    [Fact]
    public void WhenNoOptionalFunctionIsActiveAnymore_ThenAllOptionalFunctionSubjectsAreRemoved()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        heatPump.UpdateFunctionSubjects(null);

        // Act
        heatPump.UpdateFunctionSubjects(LuxtronikFunctionMask.Of());

        // Assert
        Assert.Null(heatPump.Cooling);
        Assert.Null(heatPump.Pool);
        Assert.Null(heatPump.Solar);
        Assert.Null(heatPump.RoomControl);
        Assert.Null(heatPump.MixingCircuit1);
        Assert.Null(heatPump.MixingCircuit2);
        Assert.Null(heatPump.MixingCircuit3);
    }

    [Fact]
    public void WhenActiveFunctionsAreUnknown_ThenAllOptionalFunctionSubjectsExist()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.UpdateFunctionSubjects(null);

        // Assert
        Assert.NotNull(heatPump.Cooling);
        Assert.NotNull(heatPump.Pool);
        Assert.NotNull(heatPump.Solar);
        Assert.NotNull(heatPump.RoomControl);
        Assert.NotNull(heatPump.MixingCircuit1);
        Assert.NotNull(heatPump.MixingCircuit2);
        Assert.NotNull(heatPump.MixingCircuit3);
    }

    [Theory]
    [InlineData(LuxtronikFunction.Heating)]
    [InlineData(LuxtronikFunction.HotWater)]
    [InlineData(LuxtronikFunction.Cooling)]
    [InlineData(LuxtronikFunction.Pool)]
    [InlineData(LuxtronikFunction.Solar)]
    [InlineData(LuxtronikFunction.RoomControlUnit)]
    [InlineData(LuxtronikFunction.MixingCircuit1Heating)]
    [InlineData(LuxtronikFunction.MixingCircuit1Cooling)]
    [InlineData(LuxtronikFunction.MixingCircuit2Heating)]
    [InlineData(LuxtronikFunction.MixingCircuit2Cooling)]
    [InlineData(LuxtronikFunction.MixingCircuit3Heating)]
    [InlineData(LuxtronikFunction.MixingCircuit3Cooling)]
    public void WhenOneFunctionFlagIsSetFromSource_ThenOnlyItsRegisterIsSetAndTheMaskRoundTrips(LuxtronikFunction function)
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        using var source = CreateSource(heatPump);
        var functionMask = LuxtronikFunctionMask.Of(function);

        // Act
        heatPump.Functions.SetFromSource(source, functionMask);

        // Assert
        Assert.Equal(functionMask, heatPump.Functions.GetFunctionMask());
        var setProperty = Assert.Single(typeof(LuxtronikFunctions).GetProperties(),
            property => property.GetValue(heatPump.Functions) is true);
        Assert.Equal((int)function, setProperty.GetCustomAttribute<ModbusRegisterAttribute>()?.Address);
    }

    [Fact]
    public void WhenMixedFunctionFlagsAreSetFromSource_ThenTheMaskRoundTrips()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        using var source = CreateSource(heatPump);
        var functionMask = LuxtronikFunctionMask.Of(
            LuxtronikFunction.Heating, LuxtronikFunction.Pool, LuxtronikFunction.RoomControlUnit,
            LuxtronikFunction.MixingCircuit1Cooling, LuxtronikFunction.MixingCircuit3Heating);

        // Act
        heatPump.Functions.SetFromSource(source, functionMask);

        // Assert
        Assert.Equal(functionMask, heatPump.Functions.GetFunctionMask());
    }

    private static ModbusSubjectClientSource CreateSource(LuxtronikHeatPump heatPump)
        => heatPump.CreateModbusClientSource(new ModbusClientConfiguration { Host = "127.0.0.1" }, NullLogger.Instance);

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
