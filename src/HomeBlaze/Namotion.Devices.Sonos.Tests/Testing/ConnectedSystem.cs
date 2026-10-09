using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A running SonosSystem connected to a FakeSonosSpeaker, with events delivered over loopback.
/// </summary>
internal sealed class ConnectedSystem : IAsyncDisposable
{
    internal static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private const int MaxStartAttempts = 3;

    private ConnectedSystem(SonosSystem system, SonosPlayer player)
    {
        System = system;
        Player = player;
    }

    internal SonosSystem System { get; }

    internal SonosPlayer Player { get; }

    internal static SonosSystem CreateSystem(string seedHost, int? eventPort = null, ILogger<SonosSystem>? logger = null, TimeProvider? clock = null) =>
        new(new TestHttpClientFactory(), logger ?? NullLogger<SonosSystem>.Instance)
        {
            Clock = clock ?? TimeProvider.System,
            SeedHost = seedHost,
            EventCallbackHost = "127.0.0.1",
            EventListenHost = "127.0.0.1",
            EventPort = eventPort ?? LoopbackHttpServer.GetFreePort(),
            RetryInterval = TimeSpan.FromSeconds(1),
            MinimumInterval = TimeSpan.FromMilliseconds(100),

            // Tests never search the real network for speakers.
            DiscoverSpeakerAsync = _ => Task.FromResult<Uri?>(null)
        };

    internal static async Task<ConnectedSystem> StartAsync(
        FakeSonosSpeaker speaker,
        string uuid = TestFixtures.KitchenUuid,
        string room = "Küche",
        Action<SonosSystem>? configure = null,
        ILogger<SonosSystem>? logger = null,
        TimeProvider? clock = null)
    {
        speaker.RespondAsIdlePlayer(uuid, room);
        var system = await StartWithEventsAsync(
            () =>
            {
                var system = CreateSystem(speaker.Host, logger: logger, clock: clock);
                configure?.Invoke(system);
                return system;
            },
            system => system.IsConnected && system.Players.TryGetValue(uuid, out var player) && player.Model is not null && system.AreEventsActive,
            "The system should connect to the fake speaker and subscribe to its events.");

        return new ConnectedSystem(system, system.Players[uuid]);
    }

    /// <summary>
    /// Starts a system from <paramref name="create"/> and waits until it is ready. The system binds its event port
    /// itself, after the free port was chosen, so a system that connected without its listener is replaced by one on
    /// another port. A system that does not become ready is stopped and disposed.
    /// </summary>
    internal static async Task<SonosSystem> StartWithEventsAsync(Func<SonosSystem> create, Func<SonosSystem, bool> isReady, string message)
    {
        for (var attempt = 1; ; attempt++)
        {
            var system = create();
            try
            {
                await system.StartAsync(CancellationToken.None);
                await AsyncTestHelpers.WaitUntilAsync(
                    () => isReady(system) || (system.IsConnected && system.ActiveEventCallbackHost is null),
                    WaitTimeout,
                    message: message);

                if (isReady(system))
                {
                    return system;
                }

                if (attempt == MaxStartAttempts)
                {
                    throw new InvalidOperationException($"{message} The event listener found no free port.");
                }
            }
            catch
            {
                await StopAsync(system);
                throw;
            }

            await StopAsync(system);
        }
    }

    /// <summary>
    /// Returns a scope that stops and disposes a system the test creates and starts itself.
    /// </summary>
    internal static IAsyncDisposable Own(SonosSystem system) => new SystemScope(system);

    private static async Task StopAsync(SonosSystem system)
    {
        await system.StopAsync(CancellationToken.None);
        system.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await System.StopAsync(CancellationToken.None);
        System.Dispose();
    }

    private sealed class SystemScope(SonosSystem system) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(StopAsync(system));
    }
}
