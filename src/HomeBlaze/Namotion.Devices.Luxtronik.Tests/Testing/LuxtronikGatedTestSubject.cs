using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

[InterceptorSubject]
public partial class LuxtronikGatedTestSubject : ILuxtronikGatedSubject
{
    public LuxtronikGatedTestSubject()
    {
        Ungated = null;
        FirmwareGated = null;
        FeatureGated = null;
    }

    public Version? SubjectMinimumFirmwareVersion { get; init; }

    public LuxtronikFeature SubjectFeature { get; init; } = LuxtronikFeature.None;

    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    public partial ushort? Ungated { get; set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16, MinimumFirmware = "3.93.0")]
    public partial ushort? FirmwareGated { get; set; }

    [LuxtronikHoldingRegister(2, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    public partial ushort? FeatureGated { get; set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => SubjectMinimumFirmwareVersion;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => SubjectFeature;
}
