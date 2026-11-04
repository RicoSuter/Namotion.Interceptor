using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

[InterceptorSubject]
public partial class LuxtronikGatedTestSubject : ILuxtronikRequirements
{
    public LuxtronikGatedTestSubject()
    {
        Ungated = null;
        FirmwareGated = null;
        FunctionGated = null;
    }

    public Version? SubjectMinimumFirmwareVersion { get; init; }

    public LuxtronikFunction SubjectFunction { get; init; } = LuxtronikFunction.None;

    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    public partial ushort? Ungated { get; set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16, MinimumFirmware = "3.93.0")]
    public partial ushort? FirmwareGated { get; set; }

    [LuxtronikHoldingRegister(2, ModbusDataType.U16, RequiredFunction = LuxtronikFunction.Cooling)]
    public partial ushort? FunctionGated { get; set; }

    Version? ILuxtronikRequirements.MinimumFirmwareVersion => SubjectMinimumFirmwareVersion;

    LuxtronikFunction ILuxtronikRequirements.RequiredFunction => SubjectFunction;
}
