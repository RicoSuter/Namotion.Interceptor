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
    /// The address of its device description from its answer, a plain HTTP address on <see cref="HostAddress"/>, or
    /// null when the answer named another scheme or user information.
    /// </summary>
    public Uri? DescriptionLocation { get; init; }

    /// <summary>
    /// The room name from its device description, or null when it was not read.
    /// </summary>
    public string? RoomName { get; init; }

    /// <summary>
    /// The model name from its device description, such as <c>Sonos One</c>, or null when it was not read.
    /// </summary>
    public string? ModelName { get; init; }
}
