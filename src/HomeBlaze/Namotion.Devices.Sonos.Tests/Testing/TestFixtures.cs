namespace Namotion.Devices.Sonos.Tests.Testing;

internal static class TestFixtures
{
    internal const string LivingRoomUuid = "RINCON_A0000000000101400";
    internal const string TerraceUuid = "RINCON_A0000000000501400";
    internal const string KitchenUuid = "RINCON_A0000000000601400";
    internal const string OfficeUuid = "RINCON_A0000000000701400";

    internal static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
