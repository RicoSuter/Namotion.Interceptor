namespace Namotion.Devices.Sonos;

/// <summary>
/// A playable Sonos favorite. Play it with <see cref="SonosPlayer.PlayFavoriteAsync"/> by <see cref="Title"/>.
/// </summary>
/// <param name="Title">The name shown in the Sonos app, matched case-insensitively by <see cref="SonosPlayer.PlayFavoriteAsync"/>.</param>
/// <param name="Uri">The UPnP URI the favorite starts.</param>
/// <param name="IsContainer">Whether the favorite is a playlist or album, which is played through the queue.</param>
/// <param name="ImageUri">The absolute URI of the cover art, or null when the favorite has none.</param>
public sealed record SonosFavorite(string Title, string Uri, bool IsContainer, string? ImageUri)
{
    // The DIDL needed to play the favorite. It often embeds account tokens, so it stays off the public surface
    // and out of JSON. It is part of record equality, so a changed token replaces the stored array.
    internal string Metadata { get; init; } = "";

    /// <summary>
    /// Returns <see cref="Title"/>, so a list of favorites reads as the names shown in the Sonos app.
    /// </summary>
    public override string ToString() => Title;
}
