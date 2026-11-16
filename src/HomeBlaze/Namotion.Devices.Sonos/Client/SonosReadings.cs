using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// One full poll of a player. Position and sleep timer are never evented, so they come only from here and from a
/// <see cref="SonosProgressReading"/>. A read the speaker answered with a fault leaves its values unknown: null, or for
/// the position and sleep timer, <see cref="HasPosition"/> and <see cref="HasSleepTimer"/> false.
/// </summary>
internal sealed record SonosPlayerReading(
    AvTransportChange AvTransport,
    TimeSpan? Position,
    TimeSpan? SleepTimerRemaining,
    RenderingControlChange RenderingControl,
    bool HasPosition = true,
    bool HasSleepTimer = true);

/// <summary>
/// One progress poll of a player: the position and sleep timer, each only when it was read and answered. A value not
/// read or answered with a fault has <see cref="HasPosition"/> or <see cref="HasSleepTimer"/> false.
/// </summary>
internal readonly record struct SonosProgressReading(
    TimeSpan? Position,
    bool HasPosition,
    TimeSpan? SleepTimerRemaining,
    bool HasSleepTimer);

/// <summary>
/// A read that started to answer with a UPnP fault: the speaker is reachable, but cannot report that value now.
/// </summary>
internal sealed record SonosReadFault(string Action, Exception Exception);

internal sealed record SonosZoneInfo(string? SerialNumber, string? MacAddress, string? HardwareVersion, string? DisplayVersion);
