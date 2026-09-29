using System.Globalization;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikRegisterMapTests
{
    private const string Fw392 = "3.92.0";
    private const string Fw3921 = "3.92.1";
    private const LuxtronikFunction NoFunction = LuxtronikFunction.None;
    private const LuxtronikFunction Circuit = LuxtronikFunction.MixingCircuit1Heating;

    // Path, space, address, data type, scale, minimum firmware, register function gate. Source: SHI manual 83026900aDE.
    private static readonly (string Path, ModbusAddressSpace Space, int Address, ModbusDataType DataType, double Scale, string? Firmware, LuxtronikFunction Function)[] ExpectedRegisters =
    [
        ("OperatingStatus.HeatPumpStatus", ModbusAddressSpace.InputRegister, 10000, ModbusDataType.U16, 1, null, NoFunction),
        ("OperatingStatus.OperatingState", ModbusAddressSpace.InputRegister, 10002, ModbusDataType.U16, 1, null, NoFunction),
        ("OperatingStatus.ErrorNumber", ModbusAddressSpace.InputRegister, 10201, ModbusDataType.U16, 1, null, NoFunction),
        ("OperatingStatus.BufferType", ModbusAddressSpace.InputRegister, 10202, ModbusDataType.U16, 1, null, NoFunction),
        ("OperatingStatus.MinimumOffTime", ModbusAddressSpace.InputRegister, 10203, ModbusDataType.U16, 1, null, NoFunction),
        ("OperatingStatus.MinimumRunTime", ModbusAddressSpace.InputRegister, 10204, ModbusDataType.U16, 1, null, NoFunction),
        ("OperatingStatus.OperatingHours", ModbusAddressSpace.InputRegister, 10404, ModbusDataType.U32, 1, Fw392, NoFunction),
        ("OperatingStatus.BrinePump.IsOn", ModbusAddressSpace.InputRegister, 10350, ModbusDataType.U16, 1, Fw392, NoFunction),

        ("Temperatures.Return.Temperature", ModbusAddressSpace.InputRegister, 10100, ModbusDataType.S16, 0.1, null, NoFunction),
        ("Temperatures.Flow.Temperature", ModbusAddressSpace.InputRegister, 10105, ModbusDataType.S16, 0.1, null, NoFunction),
        ("Temperatures.Outside.Temperature", ModbusAddressSpace.InputRegister, 10108, ModbusDataType.S16, 0.1, null, NoFunction),
        ("Temperatures.OutsideAverage.Temperature", ModbusAddressSpace.InputRegister, 10109, ModbusDataType.S16, 0.1, Fw392, NoFunction),
        ("Temperatures.HeatSourceInlet.Temperature", ModbusAddressSpace.InputRegister, 10110, ModbusDataType.S16, 0.1, Fw392, NoFunction),
        ("Temperatures.HeatSourceOutlet.Temperature", ModbusAddressSpace.InputRegister, 10111, ModbusDataType.S16, 0.1, Fw392, NoFunction),
        ("Temperatures.MaximumFlowTemperature", ModbusAddressSpace.InputRegister, 10112, ModbusDataType.S16, 0.1, Fw392, NoFunction),

        ("Energy.ThermalPower", ModbusAddressSpace.InputRegister, 10300, ModbusDataType.S16, 100, null, NoFunction),
        ("Energy.ElectricalPower", ModbusAddressSpace.InputRegister, 10301, ModbusDataType.U16, 100, null, NoFunction),
        ("Energy.MinimumPredictedElectricalPower", ModbusAddressSpace.InputRegister, 10302, ModbusDataType.U16, 100, null, NoFunction),
        ("Energy.TotalElectricalEnergy", ModbusAddressSpace.InputRegister, 10310, ModbusDataType.S32, 100, null, NoFunction),
        ("Energy.TotalThermalEnergy", ModbusAddressSpace.InputRegister, 10320, ModbusDataType.S32, 100, Fw392, NoFunction),

        ("SmartGrid.Evu1", ModbusAddressSpace.InputRegister, 10360, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("SmartGrid.Evu2", ModbusAddressSpace.InputRegister, 10361, ModbusDataType.U16, 1, Fw392, NoFunction),

        ("PowerConsumptionLimit.Mode", ModbusAddressSpace.HoldingRegister, 10040, ModbusDataType.U16, 1, null, NoFunction),
        ("PowerConsumptionLimit.Limit", ModbusAddressSpace.HoldingRegister, 10041, ModbusDataType.U16, 100, null, NoFunction),

        ("Functions.IsHeatingEnabled", ModbusAddressSpace.DiscreteInput, 10000, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsHotWaterEnabled", ModbusAddressSpace.DiscreteInput, 10001, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsCoolingEnabled", ModbusAddressSpace.DiscreteInput, 10002, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsPoolEnabled", ModbusAddressSpace.DiscreteInput, 10003, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsSolarConfigured", ModbusAddressSpace.DiscreteInput, 10004, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsRoomControlUnitConfigured", ModbusAddressSpace.DiscreteInput, 10005, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsMixingCircuit1HeatingEnabled", ModbusAddressSpace.DiscreteInput, 10006, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsMixingCircuit1CoolingEnabled", ModbusAddressSpace.DiscreteInput, 10007, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsMixingCircuit2HeatingEnabled", ModbusAddressSpace.DiscreteInput, 10008, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsMixingCircuit2CoolingEnabled", ModbusAddressSpace.DiscreteInput, 10009, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsMixingCircuit3HeatingEnabled", ModbusAddressSpace.DiscreteInput, 10010, ModbusDataType.Boolean, 1, null, NoFunction),
        ("Functions.IsMixingCircuit3CoolingEnabled", ModbusAddressSpace.DiscreteInput, 10011, ModbusDataType.Boolean, 1, null, NoFunction),

        ("Heating.Status", ModbusAddressSpace.InputRegister, 10003, ModbusDataType.U16, 1, null, NoFunction),
        ("Heating.ExternalReturn.Temperature", ModbusAddressSpace.InputRegister, 10102, ModbusDataType.S16, 0.1, null, NoFunction),
        ("Heating.ReturnTarget", ModbusAddressSpace.InputRegister, 10101, ModbusDataType.U16, 0.1, null, NoFunction),
        ("Heating.ReturnLimit", ModbusAddressSpace.InputRegister, 10103, ModbusDataType.S16, 0.1, null, NoFunction),
        ("Heating.MinimumReturnTarget", ModbusAddressSpace.InputRegister, 10104, ModbusDataType.S16, 0.1, null, NoFunction),
        ("Heating.LimitTemperature", ModbusAddressSpace.InputRegister, 10107, ModbusDataType.S16, 0.1, null, NoFunction),
        ("Heating.CalculatedFlowTemperature", ModbusAddressSpace.InputRegister, 10113, ModbusDataType.S16, 0.1, Fw392, NoFunction),
        ("Heating.Locked", ModbusAddressSpace.HoldingRegister, 10050, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("Heating.CirculationPump.IsOn", ModbusAddressSpace.InputRegister, 10354, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("Heating.OperatingHours", ModbusAddressSpace.InputRegister, 10406, ModbusDataType.U32, 1, Fw392, NoFunction),
        ("Heating.ElectricalEnergy", ModbusAddressSpace.InputRegister, 10312, ModbusDataType.S32, 100, null, NoFunction),
        ("Heating.ThermalEnergy", ModbusAddressSpace.InputRegister, 10322, ModbusDataType.S32, 100, Fw392, NoFunction),
        ("Heating.SmartHomeControl.Mode", ModbusAddressSpace.HoldingRegister, 10000, ModbusDataType.U16, 1, null, NoFunction),
        ("Heating.SmartHomeControl.Setpoint", ModbusAddressSpace.HoldingRegister, 10001, ModbusDataType.U16, 0.1, null, NoFunction),
        ("Heating.SmartHomeControl.Offset", ModbusAddressSpace.HoldingRegister, 10002, ModbusDataType.S16, 0.1, null, NoFunction),
        ("Heating.SmartHomeControl.Level", ModbusAddressSpace.HoldingRegister, 10003, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("Heating.OverallSmartHomeControl.Mode", ModbusAddressSpace.HoldingRegister, 10065, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("Heating.OverallSmartHomeControl.Offset", ModbusAddressSpace.HoldingRegister, 10066, ModbusDataType.S16, 0.1, Fw392, NoFunction),
        ("Heating.OverallSmartHomeControl.Level", ModbusAddressSpace.HoldingRegister, 10067, ModbusDataType.U16, 1, Fw392, NoFunction),

        ("HotWater.Status", ModbusAddressSpace.InputRegister, 10004, ModbusDataType.U16, 1, null, NoFunction),
        ("HotWater.Temperature.Temperature", ModbusAddressSpace.InputRegister, 10120, ModbusDataType.S16, 0.1, null, NoFunction),
        ("HotWater.Target", ModbusAddressSpace.InputRegister, 10121, ModbusDataType.U16, 0.1, null, NoFunction),
        ("HotWater.MinimumTarget", ModbusAddressSpace.InputRegister, 10122, ModbusDataType.S16, 0.1, null, NoFunction),
        ("HotWater.MaximumTarget", ModbusAddressSpace.InputRegister, 10123, ModbusDataType.S16, 0.1, null, NoFunction),
        ("HotWater.LimitTemperature", ModbusAddressSpace.InputRegister, 10124, ModbusDataType.S16, 0.1, null, NoFunction),
        ("HotWater.Locked", ModbusAddressSpace.HoldingRegister, 10051, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("HotWater.LoadingPump.IsOn", ModbusAddressSpace.InputRegister, 10355, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("HotWater.CirculationPump.IsOn", ModbusAddressSpace.InputRegister, 10356, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("HotWater.CirculationRequested", ModbusAddressSpace.HoldingRegister, 10070, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("HotWater.OperatingHours", ModbusAddressSpace.InputRegister, 10408, ModbusDataType.U32, 1, Fw392, NoFunction),
        ("HotWater.ElectricalEnergy", ModbusAddressSpace.InputRegister, 10314, ModbusDataType.S32, 100, null, NoFunction),
        ("HotWater.ThermalEnergy", ModbusAddressSpace.InputRegister, 10324, ModbusDataType.S32, 100, Fw392, NoFunction),
        ("HotWater.SmartHomeControl.Mode", ModbusAddressSpace.HoldingRegister, 10005, ModbusDataType.U16, 1, null, NoFunction),
        ("HotWater.SmartHomeControl.Setpoint", ModbusAddressSpace.HoldingRegister, 10006, ModbusDataType.U16, 0.1, null, NoFunction),
        ("HotWater.SmartHomeControl.Offset", ModbusAddressSpace.HoldingRegister, 10007, ModbusDataType.S16, 0.1, null, NoFunction),
        ("HotWater.SmartHomeControl.Level", ModbusAddressSpace.HoldingRegister, 10008, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("HotWater.ExtraHotWater.Requested", ModbusAddressSpace.HoldingRegister, 10071, ModbusDataType.U16, 1, Fw392, NoFunction),
        ("HotWater.ExtraHotWater.Target", ModbusAddressSpace.InputRegister, 10500, ModbusDataType.S16, 0.1, Fw392, NoFunction),
        ("HotWater.ExtraHotWater.Duration", ModbusAddressSpace.InputRegister, 10501, ModbusDataType.S16, 1, Fw392, NoFunction),
        ("HotWater.ExtraHotWater.RemainingDuration", ModbusAddressSpace.InputRegister, 10502, ModbusDataType.S16, 1, Fw392, NoFunction),

        ("Cooling.Status", ModbusAddressSpace.InputRegister, 10006, ModbusDataType.U16, 1, null, NoFunction),
        ("Cooling.Released", ModbusAddressSpace.InputRegister, 10207, ModbusDataType.U16, 1, null, NoFunction),
        ("Cooling.Locked", ModbusAddressSpace.HoldingRegister, 10052, ModbusDataType.U16, 1, null, NoFunction),
        ("Cooling.OperatingHours", ModbusAddressSpace.InputRegister, 10410, ModbusDataType.U32, 1, Fw392, NoFunction),
        ("Cooling.ElectricalEnergy", ModbusAddressSpace.InputRegister, 10316, ModbusDataType.S32, 100, null, NoFunction),
        ("Cooling.ThermalEnergy", ModbusAddressSpace.InputRegister, 10326, ModbusDataType.S32, 100, Fw392, NoFunction),

        ("Pool.Status", ModbusAddressSpace.InputRegister, 10007, ModbusDataType.U16, 1, null, NoFunction),
        ("Pool.Locked", ModbusAddressSpace.HoldingRegister, 10053, ModbusDataType.U16, 1, null, NoFunction),
        ("Pool.OperatingHours", ModbusAddressSpace.InputRegister, 10412, ModbusDataType.U32, 1, Fw392, NoFunction),
        ("Pool.ElectricalEnergy", ModbusAddressSpace.InputRegister, 10318, ModbusDataType.S32, 100, null, NoFunction),
        ("Pool.ThermalEnergy", ModbusAddressSpace.InputRegister, 10328, ModbusDataType.S32, 100, Fw392, NoFunction),

        ("Solar.OperatingHours", ModbusAddressSpace.InputRegister, 10416, ModbusDataType.U32, 1, Fw392, NoFunction),

        ("RoomControl.Temperature.Temperature", ModbusAddressSpace.InputRegister, 10106, ModbusDataType.S16, 0.1, null, NoFunction),
        ("RoomControl.TemperatureSetpoint", ModbusAddressSpace.HoldingRegister, 10060, ModbusDataType.U16, 0.1, Fw3921, NoFunction),

        .. GetCircuitRegisters(1),
        .. GetCircuitRegisters(2),
        .. GetCircuitRegisters(3)
    ];

    private static (string, ModbusAddressSpace, int, ModbusDataType, double, string?, LuxtronikFunction)[] GetCircuitRegisters(int index)
    {
        var name = $"MixingCircuit{index}";
        var offset = (index - 1) * 10;
        return
        [
            ($"{name}.Temperature.Temperature", ModbusAddressSpace.InputRegister, 10140 + offset, ModbusDataType.S16, 0.1, null, NoFunction),
            ($"{name}.Target", ModbusAddressSpace.InputRegister, 10141 + offset, ModbusDataType.S16, 0.1, null, NoFunction),
            ($"{name}.MinimumTarget", ModbusAddressSpace.InputRegister, 10142 + offset, ModbusDataType.S16, 0.1, null, Circuit),
            ($"{name}.MaximumTarget", ModbusAddressSpace.InputRegister, 10143 + offset, ModbusDataType.S16, 0.1, null, Circuit),
            ($"{name}.Pump.IsOn", ModbusAddressSpace.InputRegister, 10350 + index, ModbusDataType.U16, 1, Fw392, NoFunction),
            ($"{name}.HeatingSmartHomeControl.Mode", ModbusAddressSpace.HoldingRegister, 10010 + offset, ModbusDataType.U16, 1, null, NoFunction),
            ($"{name}.HeatingSmartHomeControl.Setpoint", ModbusAddressSpace.HoldingRegister, 10011 + offset, ModbusDataType.U16, 0.1, null, NoFunction),
            ($"{name}.HeatingSmartHomeControl.Offset", ModbusAddressSpace.HoldingRegister, 10012 + offset, ModbusDataType.S16, 0.1, null, NoFunction),
            ($"{name}.HeatingSmartHomeControl.Level", ModbusAddressSpace.HoldingRegister, 10013 + offset, ModbusDataType.U16, 1, Fw392, NoFunction),
            ($"{name}.CoolingSmartHomeControl.Mode", ModbusAddressSpace.HoldingRegister, 10015 + offset, ModbusDataType.U16, 1, null, NoFunction),
            ($"{name}.CoolingSmartHomeControl.Setpoint", ModbusAddressSpace.HoldingRegister, 10016 + offset, ModbusDataType.U16, 0.1, null, NoFunction),
            ($"{name}.CoolingSmartHomeControl.Offset", ModbusAddressSpace.HoldingRegister, 10017 + offset, ModbusDataType.S16, 0.1, null, NoFunction)
        ];
    }

    [Fact]
    public void WhenAllFunctionsExist_ThenEveryRegisterMatchesTheManual()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        heatPump.UpdateFunctionSubjects(activeFunctions: null);

        // Act
        var actual = GetRegisters(heatPump.TryGetRegisteredSubject()!, string.Empty)
            .Select(Format)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Assert
        Assert.Equal(ExpectedRegisters.Select(Format).Order(StringComparer.Ordinal).ToList(), actual);
    }

    [Fact]
    public void WhenAllFunctionsExist_ThenNoTwoRegistersOverlap()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        heatPump.UpdateFunctionSubjects(activeFunctions: null);

        // Act
        var overlaps = GetRegisters(heatPump.TryGetRegisteredSubject()!, string.Empty)
            .SelectMany(register => Enumerable
                .Range(register.Address, register.DataType is ModbusDataType.U32 or ModbusDataType.S32 ? 2 : 1)
                .Select(address => (register.Space, Address: address, register.Path)))
            .GroupBy(word => (word.Space, word.Address))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Space} {group.Key.Address}: {string.Join(", ", group.Select(word => word.Path))}")
            .ToList();

        // Assert
        Assert.Empty(overlaps);
    }

    private static IEnumerable<(string Path, ModbusAddressSpace Space, int Address, ModbusDataType DataType, double Scale, string? Firmware, LuxtronikFunction Function)> GetRegisters(
        RegisteredSubject subject, string prefix)
    {
        var baseAddress = subject.Subject is IModbusBaseAddressProvider provider ? provider.BaseAddress : 0;
        foreach (var property in subject.Properties)
        {
            var path = prefix + property.Name;
            if (property.ReflectionAttributes.OfType<ModbusRegisterAttribute>().FirstOrDefault() is { } attribute)
            {
                var function = attribute is LuxtronikRegisterAttribute luxtronikAttribute ? luxtronikAttribute.Function : NoFunction;
                yield return (path, attribute.Space, baseAddress + attribute.Address, attribute.DataType, attribute.Scale, GetMinimumFirmware(property), function);
            }

            var childRegisters = property.Children
                .Select(child => child.Subject.TryGetRegisteredSubject())
                .OfType<RegisteredSubject>()
                .SelectMany(child => GetRegisters(child, path + "."));
            foreach (var register in childRegisters)
            {
                yield return register;
            }
        }
    }

    // The effective firmware gate of the register and its subject, probed with the gating itself.
    private static string? GetMinimumFirmware(RegisteredSubjectProperty property)
    {
        if (LuxtronikGating.IsSupported(property, new Version(3, 90, 1), activeFunctions: null))
        {
            return null;
        }

        return LuxtronikGating.IsSupported(property, new Version(3, 92, 0), activeFunctions: null) ? Fw392 : Fw3921;
    }

    private static string Format((string Path, ModbusAddressSpace Space, int Address, ModbusDataType DataType, double Scale, string? Firmware, LuxtronikFunction Function) register)
        => string.Create(CultureInfo.InvariantCulture,
            $"{register.Path} {register.Space} {register.Address} {register.DataType} x{register.Scale} fw={register.Firmware ?? "any"} {register.Function}");
}
