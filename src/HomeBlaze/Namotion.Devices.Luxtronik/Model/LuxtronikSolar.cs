// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Solar. Present while the solar flag is set.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikSolar
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikSolar()
    {
        OperatingHours = null;
    }

    /// <summary>
    /// Gets the operating hours of solar.
    /// </summary>
    [LuxtronikInputRegister(10416, ModbusDataType.U32, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 1)]
    public partial decimal? OperatingHours { get; internal set; }
}
