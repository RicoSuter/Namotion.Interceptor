using System.Net;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// What the scheduled polls read while events are live and when they are not, counted as SOAP calls on a household of
/// two standalone home theater players at the default 30 second polling interval. The polls run on a stepped clock,
/// one after another, while the connection loop waits an hour for its own.
/// </summary>
public class SonosSystemPollingTests
{
    private const string AvTransportEventPath = "/MediaRenderer/AVTransport/Event";
    private const string RenderingControlEventPath = "/MediaRenderer/RenderingControl/Event";
    private const string GroupRenderingControlEventPath = "/MediaRenderer/GroupRenderingControl/Event";
    private const string TopologyEventPath = "/ZoneGroupTopology/Event";

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(SonosSystem.DefaultIntervalSeconds);

    // A full poll of a standalone home theater player: 5 AVTransport, 7 RenderingControl and 2 GroupRenderingControl
    // actions.
    private const int PlayerPollCalls = 5 + 7 + 2;

    // GetZoneGroupState from the seed, Browse for the favorites, and a full poll of each player.
    private const int FullReconcileCalls = 1 + 1 + (2 * PlayerPollCalls);

    [Fact]
    public async Task WhenNoEventArrives_ThenEveryPollReadsEverything()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: false);

        // Act
        var calls = await household.CountCallsAsync(polls: 20);

        // Assert
        Assert.Equal(20 * FullReconcileCalls, calls);
        Assert.Equal(60.0, calls / 10.0);
    }

    [Fact]
    public async Task WhenTheEventListenerDoesNotRun_ThenEveryPollReadsEverything()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: false, blocksEventPort: true);

        // Act
        var calls = await household.CountCallsAsync(polls: 20);

        // Assert
        Assert.Null(household.System.ActiveEventCallbackHost);
        Assert.Equal(20 * FullReconcileCalls, calls);
    }

    [Fact]
    public async Task WhenEventsAreLiveAndNothingPlays_ThenPollsBetweenFullReconciliationsReadOnlyTheTopology()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);

        // Act
        var calls = await household.CountCallsAsync(polls: 20);

        // Assert
        // The polls at 5 and 10 minutes are full reconciliations; the 18 others read the topology alone.
        Assert.Equal((2 * FullReconcileCalls) + 18, calls);
        Assert.Equal(7.8, calls / 10.0);
    }

    [Fact]
    public async Task WhenEventsAreLiveAndAGroupPlays_ThenPollsAlsoReadItsPositionAndSleepTimer()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true, kitchenPlays: true);
        household.Kitchen.Respond("GetPositionInfo", ("RelTime", "0:01:15"));
        household.Kitchen.Respond("GetRemainingSleepTimerDuration", ("RemainingSleepTimerDuration", "0:20:00"));

        // Act
        var calls = await household.CountCallsAsync(polls: 20);

        // Assert
        // The 18 polls between the full reconciliations read the topology, and the position and the sleep timer of
        // the playing kitchen.
        Assert.Equal((2 * FullReconcileCalls) + (18 * 3), calls);
        Assert.Equal(11.4, calls / 10.0);
        Assert.Equal(TimeSpan.FromSeconds(75), household.KitchenPlayer.CurrentTrackPosition);
        Assert.Equal(TimeSpan.FromMinutes(20), household.KitchenPlayer.SleepTimerRemaining);
    }

    [Fact]
    public async Task WhenEventsAreLiveAndAGroupPlays_ThenEachPollUpdatesItsPosition()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true, kitchenPlays: true);
        household.Kitchen.Respond("GetPositionInfo", ("RelTime", "0:02:30"));
        household.ClearCalls();

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(150), household.KitchenPlayer.CurrentTrackPosition);
        Assert.Equal(["GetPositionInfo", "GetRemainingSleepTimerDuration", "GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action).Order());
        Assert.Empty(household.Office.Calls);
    }

    [Fact]
    public async Task WhenARenewalGetsNoAnswer_ThenThePlayerIsPolledFullyUntilARenewalSucceeds()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true, fullReconcileInterval: TimeSpan.FromHours(1));
        household.Office.AbortRenewals = true;
        household.Clock.Advance(TimeSpan.FromMinutes(16));
        await household.PollAsync(advances: false);

        // Act
        household.ClearCalls();
        await household.PollAsync();

        // Assert
        Assert.Equal(PlayerPollCalls, household.Office.Calls.Count);
        Assert.Equal(["GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action));

        // Act
        household.Office.AbortRenewals = false;
        await household.PollAsync();
        household.ClearCalls();
        await household.PollAsync();

        // Assert
        Assert.Empty(household.Office.Calls);
    }

    [Fact]
    public async Task WhenSubscriptionsLapse_ThenThePlayersArePolledFully()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true, fullReconcileInterval: TimeSpan.FromHours(1));
        household.ClearCalls();

        // Act
        household.Clock.Advance(TimeSpan.FromMinutes(31));
        await household.PollAsync(advances: false);

        // Assert
        Assert.Equal(FullReconcileCalls, household.Kitchen.Calls.Count + household.Office.Calls.Count);
    }

    [Fact]
    public async Task WhenARenewalIsRejectedAndSubscribingAgainFails_ThenThePlayerIsPolledFullyUntilEventsArrive()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true, fullReconcileInterval: TimeSpan.FromHours(1));
        household.Office.FailSubscriptions = true;
        household.Clock.Advance(TimeSpan.FromMinutes(16));
        await household.PollAsync(advances: false);

        // Act
        household.ClearCalls();
        await household.PollAsync();

        // Assert
        Assert.Equal(PlayerPollCalls, household.Office.Calls.Count);
        Assert.Equal(["GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action));

        // Act
        household.Office.FailSubscriptions = false;
        await household.PollAsync();
        await household.SendEventsAsync(household.Office, AvTransportEventPath, RenderingControlEventPath, GroupRenderingControlEventPath);
        household.ClearCalls();
        await household.PollAsync();

        // Assert
        Assert.Empty(household.Office.Calls);
    }

    [Fact]
    public async Task WhenATopologyEventMissesAPlayer_ThenItIsPolledFullyAndSubscribedAgain()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        var withoutOffice = SonosEventBodies.CreateStandaloneTopology((TestFixtures.KitchenUuid, "Küche", household.Kitchen.BaseUri));
        await household.Kitchen.NotifyAsync(TopologyEventPath, SonosEventBodies.Properties(("ZoneGroupState", withoutOffice)));
        household.ClearCalls();

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(PlayerPollCalls, household.Office.Calls.Count);
        Assert.Contains(AvTransportEventPath, household.Office.Unsubscribed);
        Assert.Equal(2, household.Office.Subscribed.Count(path => path == AvTransportEventPath));
        Assert.Equal(2, household.Kitchen.Subscribed.Count(path => path == TopologyEventPath));
        Assert.Equal(1, household.Kitchen.Subscribed.Count(path => path == AvTransportEventPath));
        Assert.Equal(["GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action));
        Assert.True(household.OfficePlayer.IsConnected);
    }

    private sealed class PolledHousehold : IAsyncDisposable
    {
        private readonly LoopbackHttpServer? _portBlocker;

        private PolledHousehold(FakeSonosSpeaker kitchen, FakeSonosSpeaker office, LoopbackHttpServer? portBlocker, SteppableClock clock, SonosSystem system)
        {
            Kitchen = kitchen;
            Office = office;
            _portBlocker = portBlocker;
            Clock = clock;
            System = system;
        }

        internal FakeSonosSpeaker Kitchen { get; }

        internal FakeSonosSpeaker Office { get; }

        internal SteppableClock Clock { get; }

        internal SonosSystem System { get; }

        internal SonosPlayer KitchenPlayer => System.Players[TestFixtures.KitchenUuid];

        internal SonosPlayer OfficePlayer => System.Players[TestFixtures.OfficeUuid];

        /// <summary>
        /// Connects to the kitchen and the office as standalone players. With events, every subscription has delivered
        /// one before this returns; without, the speakers accept the subscriptions but never send one.
        /// </summary>
        internal static async Task<PolledHousehold> StartAsync(
            bool sendsEvents, bool kitchenPlays = false, bool blocksEventPort = false, TimeSpan? fullReconcileInterval = null)
        {
            var kitchen = new FakeSonosSpeaker { SendInitialEvents = sendsEvents };
            var office = new FakeSonosSpeaker { SendInitialEvents = sendsEvents };
            var portBlocker = blocksEventPort ? new LoopbackHttpServer(_ => Task.CompletedTask) : null;
            try
            {
                kitchen.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
                office.RespondAsIdlePlayer(TestFixtures.OfficeUuid, "Büro");
                foreach (var speaker in new[] { kitchen, office })
                {
                    speaker.RespondWithTopology((TestFixtures.KitchenUuid, "Küche", kitchen.BaseUri), (TestFixtures.OfficeUuid, "Büro", office.BaseUri));
                }

                if (kitchenPlays)
                {
                    kitchen.Respond("GetTransportInfo", ("CurrentTransportState", "PLAYING"), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1"));
                }

                var clock = new SteppableClock();
                SonosSystem Create() => ConnectedSystem.CreateSystem(
                    kitchen.Host,
                    eventPort: portBlocker?.Port,
                    clock: clock,
                    configure: system =>
                    {
                        // The connection loop never polls on its own; the test runs each poll.
                        system.PollingInterval = TimeSpan.FromHours(1);
                        system.FailedRenewalRetryDelay = TimeSpan.FromSeconds(1);
                        system.FullReconcileInterval = fullReconcileInterval ?? system.FullReconcileInterval;
                    });

                bool IsReady(SonosSystem system) =>
                    system.IsConnected &&
                    system.Players.Count == 2 &&
                    system.Players.Values.All(player => player.IsConnected && player.Model is not null);

                SonosSystem system;
                if (blocksEventPort)
                {
                    system = Create();
                    await system.StartAsync(CancellationToken.None);
                    await AsyncTestHelpers.WaitUntilAsync(
                        () => IsReady(system), ConnectedSystem.WaitTimeout, message: "The system should connect without events.");
                }
                else
                {
                    system = await ConnectedSystem.StartWithEventsAsync(
                        Create,
                        system => IsReady(system) && system.ActiveEventCallbackHost is not null,
                        "The system should connect to both speakers with its event listener running.");
                }

                var household = new PolledHousehold(kitchen, office, portBlocker, clock, system);
                if (sendsEvents)
                {
                    await household.SendEventsAsync(kitchen, AvTransportEventPath, RenderingControlEventPath, GroupRenderingControlEventPath, TopologyEventPath);
                    await household.SendEventsAsync(office, AvTransportEventPath, RenderingControlEventPath, GroupRenderingControlEventPath);
                }

                return household;
            }
            catch
            {
                await kitchen.DisposeAsync();
                await office.DisposeAsync();
                if (portBlocker is not null)
                {
                    await portBlocker.DisposeAsync();
                }

                throw;
            }
        }

        /// <summary>
        /// Sends an event without values to each event path, so its subscription has delivered one once this returns.
        /// </summary>
        internal async Task SendEventsAsync(FakeSonosSpeaker speaker, params string[] eventPaths)
        {
            foreach (var eventPath in eventPaths)
            {
                Assert.Equal(HttpStatusCode.OK, await speaker.NotifyAsync(eventPath, SonosEventBodies.Properties()));
            }
        }

        /// <summary>
        /// Runs a scheduled poll, a polling interval after the previous one unless <paramref name="advances"/> is false.
        /// </summary>
        internal async Task PollAsync(bool advances = true)
        {
            if (advances)
            {
                Clock.Advance(PollingInterval);
            }

            await System.PollAsync(CancellationToken.None);
        }

        /// <summary>
        /// Runs the polls one polling interval apart and returns how many SOAP calls both speakers received.
        /// </summary>
        internal async Task<int> CountCallsAsync(int polls)
        {
            var before = Kitchen.Calls.Count + Office.Calls.Count;
            for (var poll = 0; poll < polls; poll++)
            {
                await PollAsync();
            }

            return Kitchen.Calls.Count + Office.Calls.Count - before;
        }

        internal void ClearCalls()
        {
            Kitchen.ClearCalls();
            Office.ClearCalls();
        }

        public async ValueTask DisposeAsync()
        {
            await ConnectedSystem.StopAsync(System);
            await Kitchen.DisposeAsync();
            await Office.DisposeAsync();
            if (_portBlocker is not null)
            {
                await _portBlocker.DisposeAsync();
            }
        }
    }
}
