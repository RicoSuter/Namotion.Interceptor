namespace Namotion.ZWave.SerialApi;

internal static class FunctionId
{
    public const byte GetSerialApiInitData = 0x02;
    public const byte GetControllerCapabilities = 0x05;
    public const byte GetSerialApiCapabilities = 0x07;
    public const byte GetControllerVersion = 0x15;
    public const byte GetControllerId = 0x20;
}
