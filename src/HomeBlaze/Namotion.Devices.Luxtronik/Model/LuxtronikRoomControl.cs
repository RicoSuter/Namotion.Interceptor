// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Room control unit (RBE). Present while the room control unit flag is set.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikRoomControl
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikRoomControl()
    {
        Temperature = new LuxtronikTemperatureSensor(10106, "Room temperature");
        TemperatureSetpoint = null;
    }

    /// <summary>
    /// Gets the room temperature setpoint sent over the SHI.
    /// </summary>
    [LuxtronikHoldingRegister(10060, ModbusDataType.U16, Scale = 0.1, MinimumFirmware = "3.92.1")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal? TemperatureSetpoint { get; internal set; }

    /// <summary>
    /// Gets the room temperature measured by the room control unit.
    /// </summary>
    [State(Position = 20)]
    public partial LuxtronikTemperatureSensor Temperature { get; internal set; }
}
