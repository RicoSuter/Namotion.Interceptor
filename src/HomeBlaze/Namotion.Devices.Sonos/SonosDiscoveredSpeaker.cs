namespace Namotion.Devices.Sonos;

/// <summary>
/// A Sonos speaker that answered an SSDP search, a room player or a satellite.
/// </summary>
/// <param name="Id">The RINCON ID of the speaker, such as <c>RINCON_000E58A0123401400</c>.</param>
/// <param name="HostAddress">The host of its device description, as <see cref="Uri.Host"/> returns it.</param>
/// <param name="Port">The port of its device description.</param>
public sealed record SonosDiscoveredSpeaker(string Id, string HostAddress, int Port);
