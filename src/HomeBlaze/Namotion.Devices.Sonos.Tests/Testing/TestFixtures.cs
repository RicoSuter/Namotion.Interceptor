namespace Namotion.Devices.Sonos.Tests.Testing;

internal static class TestFixtures
{
    internal const string LivingRoomUuid = "RINCON_A0000000000101400";
    internal const string TerraceUuid = "RINCON_A0000000000501400";
    internal const string KitchenUuid = "RINCON_A0000000000601400";
    internal const string OfficeUuid = "RINCON_A0000000000701400";

    /// <summary>
    /// The transport URI of a Spotify Connect session on the kitchen player, with a placeholder session id.
    /// </summary>
    /// <summary>
    /// A poll or event order, as <see cref="SonosSystem.NextOrder"/> hands out, for tests that order them by hand.
    /// </summary>
    internal const long T0 = 1000;

    internal const string SpotifyConnectUri = "x-sonos-vli:RINCON_A0000000000601400:2,spotify:0000000000000000";

    internal static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
