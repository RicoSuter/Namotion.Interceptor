using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// One poll of a player. Position and sleep timer are never evented, so they come only from here. A read the
/// speaker answered with a fault leaves its values unknown: null, or for the position and sleep timer,
/// <see cref="HasPosition"/> and <see cref="HasSleepTimer"/> false.
/// </summary>
internal sealed record SonosPlayerReading(
    AvTransportChange AvTransport,
    TimeSpan? Position,
    TimeSpan? SleepTimerRemaining,
    RenderingControlChange RenderingControl,
    bool HasPosition = true,
    bool HasSleepTimer = true);

/// <summary>
/// A read that started to answer with a UPnP fault: the speaker is reachable, but cannot report that value now.
/// </summary>
internal sealed record SonosReadFault(string Action, Exception Exception);

internal sealed record SonosZoneInfo(string? SerialNumber, string? MacAddress, string? HardwareVersion, string? DisplayVersion);
