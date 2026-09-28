// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Room temperature setpoint override (holding 10060), firmware 3.92.1 and later with a room control unit.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikRoomControl : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private static readonly Version Firmware3921 = new(3, 92, 1);

    public LuxtronikRoomControl()
    {
        TemperatureSetpoint = null;
    }

    public int BaseAddress => 10060;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal? TemperatureSetpoint { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => Firmware3921;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.RoomControlUnit;
}
