namespace Namotion.ZWave.SerialApi;

internal enum FrameKind
{
    Ack,
    Nak,
    Can,
    Data,
    ChecksumError
}
