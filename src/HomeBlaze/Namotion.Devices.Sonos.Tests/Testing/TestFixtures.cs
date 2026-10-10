using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos.Tests.Testing;

internal static class TestFixtures
{
    internal const string LivingRoomUuid = "RINCON_A0000000000101400";
    internal const string TerraceUuid = "RINCON_A0000000000501400";
    internal const string KitchenUuid = "RINCON_A0000000000601400";
    internal const string OfficeUuid = "RINCON_A0000000000701400";

    /// <summary>
    /// A poll or event order, as <see cref="SonosSystem.NextOrder"/> hands out, for tests that order them by hand.
    /// </summary>
    internal const long T0 = 1000;

    /// <summary>
    /// The transport URI of a Spotify Connect session on the kitchen player, with a placeholder session id.
    /// </summary>
    internal const string SpotifyConnectUri = "x-sonos-vli:RINCON_A0000000000601400:2,spotify:0000000000000000";

    /// <summary>
    /// A rendering control change that reports nothing.
    /// </summary>
    internal static readonly RenderingControlChange EmptyRenderingControl = new(null, null, null, null, null, null, null);

    internal static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static SonosSystem CreateSystem(TimeProvider? clock = null) =>
        new(new TestHttpClientFactory(), NullLogger<SonosSystem>.Instance) { Clock = clock ?? TimeProvider.System };

    internal static SonosTopology ReadHousehold() =>
        ZoneGroupStateParser.Parse(Read("zone-group-state.xml"));

    /// <summary>
    /// Returns a system with the fixture household applied and not yet polled, so no device is connected.
    /// </summary>
    internal static SonosSystem CreateHousehold(TimeProvider? clock = null)
    {
        var system = CreateSystem(clock);
        system.ApplyTopology(ReadHousehold());
        return system;
    }

    /// <summary>
    /// Returns a system with the fixture household applied and every device reachable, as after its first poll.
    /// </summary>
    internal static SonosSystem CreateReachableHousehold()
    {
        var system = CreateHousehold();
        ReportAllReachable(system);
        return system;
    }

    /// <summary>
    /// Returns a connected household of the office coordinating a group with the kitchen.
    /// </summary>
    internal static SonosSystem CreateGroupedSystem()
    {
        var system = CreateSystem();
        system.ApplyTopology(ZoneGroupStateParser.Parse(SonosEventBodies.CreateGroupTopology(
            (OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/")),
            (KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/")))));
        ReportAllReachable(system);
        system.IsConnected = true;
        return system;
    }

    /// <summary>
    /// Reports every player and satellite reachable, as their first successful poll does.
    /// </summary>
    internal static void ReportAllReachable(SonosSystem system)
    {
        foreach (var device in SonosSystem.GetDevices(system.Players.Values))
        {
            device.ReportPollSucceeded();
        }
    }
}
