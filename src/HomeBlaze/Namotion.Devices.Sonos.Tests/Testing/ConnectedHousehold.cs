using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A running SonosSystem connected to two FakeSonosSpeakers, the kitchen and the office, optionally grouped
/// with the office as coordinator.
/// </summary>
internal sealed class ConnectedHousehold : IAsyncDisposable
{
    private ConnectedHousehold(FakeSonosSpeaker kitchen, FakeSonosSpeaker office, SonosSystem system)
    {
        Kitchen = kitchen;
        Office = office;
        System = system;
    }

    internal FakeSonosSpeaker Kitchen { get; }

    internal FakeSonosSpeaker Office { get; }

    internal SonosSystem System { get; }

    internal SonosPlayer KitchenPlayer => System.Players[TestFixtures.KitchenUuid];

    internal SonosPlayer OfficePlayer => System.Players[TestFixtures.OfficeUuid];

    internal static async Task<ConnectedHousehold> StartAsync(bool isGrouped = false)
    {
        var kitchen = new FakeSonosSpeaker();
        var office = new FakeSonosSpeaker();
        kitchen.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        office.RespondAsIdlePlayer(TestFixtures.OfficeUuid, "Büro");
        foreach (var speaker in new[] { kitchen, office })
        {
            if (isGrouped)
            {
                speaker.RespondWithGroup((TestFixtures.OfficeUuid, "Büro", office.BaseUri), (TestFixtures.KitchenUuid, "Küche", kitchen.BaseUri));
            }
            else
            {
                speaker.RespondWithTopology((TestFixtures.KitchenUuid, "Küche", kitchen.BaseUri), (TestFixtures.OfficeUuid, "Büro", office.BaseUri));
            }
        }

        var system = ConnectedSystem.CreateSystem(kitchen.Host);
        var household = new ConnectedHousehold(kitchen, office, system);
        try
        {
            await system.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => system.IsConnected &&
                      system.AreEventsActive &&
                      system.Players.Count == 2 &&
                      system.Players.Values.All(player => player.IsConnected && player.Model is not null),
                ConnectedSystem.WaitTimeout,
                message: "The system should connect to both speakers.");
            return household;
        }
        catch
        {
            await household.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await System.StopAsync(CancellationToken.None);
        System.Dispose();
        await Kitchen.DisposeAsync();
        await Office.DisposeAsync();
    }
}
