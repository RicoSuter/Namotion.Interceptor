namespace Namotion.ZWave.SerialApi;

internal readonly record struct DataFrame(FrameType Type, byte Function, byte[] Payload);
