namespace Namotion.ZWave.SerialApi;

internal static class FrameHeader
{
    public const byte StartOfFrame = 0x01;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;
    public const byte Can = 0x18;
}
