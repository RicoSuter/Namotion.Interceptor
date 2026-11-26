namespace Namotion.Devices.Sonos;

/// <summary>
/// A Sonos speaker that answered an SSDP search, a room player or a satellite.
/// </summary>
/// <param name="Id">The RINCON ID of the speaker, such as <c>RINCON_000E58A0123401400</c>.</param>
/// <param name="HostAddress">The host of its device description, as <see cref="Uri.Host"/> returns it.</param>
/// <param name="Port">The port of its device description.</param>
internal sealed record SonosDiscoveredSpeaker(string Id, string HostAddress, int Port)
{
    /// <summary>
    /// The room name from its device description, at most 64 characters, or null when it was not read.
    /// </summary>
    public string? RoomName { get; init; }

    /// <summary>
    /// The model name from its device description, such as <c>Sonos One</c>, at most 64 characters, or null when it
    /// was not read.
    /// </summary>
    public string? ModelName { get; init; }
}
