using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A running SonosSystem connected to a FakeSonosSpeaker, with events delivered over loopback.
/// </summary>
internal sealed class ConnectedSystem : IAsyncDisposable
{
    internal static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private ConnectedSystem(SonosSystem system, SonosPlayer player)
    {
        System = system;
        Player = player;
    }

    internal SonosSystem System { get; }

    internal SonosPlayer Player { get; }

    internal static SonosSystem CreateSystem(string seedHost, int? eventPort = null) =>
        new(new TestHttpClientFactory(), NullLogger<SonosSystem>.Instance)
        {
            SeedHost = seedHost,
            EventCallbackHost = "127.0.0.1",
            EventListenHost = "127.0.0.1",
            EventPort = eventPort ?? LoopbackHttpServer.GetFreePort(),
            RetryInterval = TimeSpan.FromSeconds(1)
        };

    internal static async Task<ConnectedSystem> StartAsync(
        FakeSonosSpeaker speaker, string uuid = TestFixtures.KitchenUuid, string room = "Küche")
    {
        speaker.RespondAsIdlePlayer(uuid, room);
        var system = CreateSystem(speaker.Host);
        await system.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.IsConnected && system.Players.TryGetValue(uuid, out var player) && player.Model is not null && system.AreEventsActive,
            WaitTimeout,
            message: "The system should connect to the fake speaker and subscribe to its events.");

        return new ConnectedSystem(system, system.Players[uuid]);
    }

    public async ValueTask DisposeAsync()
    {
        await System.StopAsync(CancellationToken.None);
        System.Dispose();
    }
}
