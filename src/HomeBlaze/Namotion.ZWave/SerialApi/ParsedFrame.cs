namespace Namotion.ZWave.SerialApi;

/// <summary>
/// A frame read by <see cref="FrameParser"/>; <see cref="Data"/> is set only for <see cref="FrameKind.Data"/>.
/// </summary>
internal readonly record struct ParsedFrame(FrameKind Kind, DataFrame Data);
