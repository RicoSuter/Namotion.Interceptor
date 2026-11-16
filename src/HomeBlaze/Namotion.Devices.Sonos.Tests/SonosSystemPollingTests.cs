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
        // 60 calls per minute.
        Assert.Equal(20 * FullReconcileCalls, calls);
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
        // The polls at 5 and 10 minutes are full reconciliations; the 18 others read the topology alone. 7.8 calls per
        // minute.
        Assert.Equal((2 * FullReconcileCalls) + 18, calls);
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
        // the playing kitchen. 11.4 calls per minute.
        Assert.Equal((2 * FullReconcileCalls) + (18 * 3), calls);
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
        // The position once, which the initial AVTransport event of the new subscription asks for.
        Assert.Equal(["GetPositionInfo"], household.Office.Calls.Select(call => call.Action));
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
        Assert.Equal(1, household.Kitchen.Subscribed.Count(path => path == TopologyEventPath));
        Assert.Equal(1, household.Kitchen.Subscribed.Count(path => path == AvTransportEventPath));
        Assert.Equal(["GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action));
        Assert.True(household.OfficePlayer.IsConnected);
    }

    [Fact]
    public async Task WhenAPollFails_ThenTheNextPollIsAFullReconciliation()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        household.Kitchen.RespondWithServerError("GetZoneGroupState");
        await Assert.ThrowsAnyAsync<Exception>(() => household.PollAsync());
        household.Kitchen.ClearServerError("GetZoneGroupState");
        household.ClearCalls();

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(FullReconcileCalls, household.Kitchen.Calls.Count + household.Office.Calls.Count);
    }

    [Fact]
    public async Task WhenAProgressPollFails_ThenTheNextPollIsAFullReconciliation()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true, kitchenPlays: true);
        household.Kitchen.RespondWithServerError("GetPositionInfo");
        await household.PollAsync();
        household.Kitchen.ClearServerError("GetPositionInfo");
        household.ClearCalls();

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(FullReconcileCalls, household.Kitchen.Calls.Count + household.Office.Calls.Count);
    }

    [Fact]
    public async Task WhenAPlayingGroupPauses_ThenThePositionIsReadOnceMore()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true, kitchenPlays: true);
        household.Kitchen.Respond("GetPositionInfo", ("RelTime", "0:01:40"));
        await household.Kitchen.NotifyAsync(AvTransportEventPath, SonosEventBodies.AvTransport(("TransportState", "PAUSED_PLAYBACK")));
        household.ClearCalls();

        // Act
        await household.PollAsync();
        var callsAfterPause = household.Kitchen.Calls.Select(call => call.Action).ToArray();
        household.ClearCalls();
        await household.PollAsync();

        // Assert
        Assert.Equal(["GetPositionInfo", "GetZoneGroupState"], callsAfterPause.Order());
        Assert.Equal(TimeSpan.FromSeconds(100), household.KitchenPlayer.CurrentTrackPosition);
        Assert.Equal(["GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action));
    }

    [Fact]
    public async Task WhenTheTrackChangesWhilePaused_ThenThePositionOfTheNewTrackIsRead()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        household.Kitchen.Respond("GetPositionInfo", ("RelTime", "0:00:00"));
        await household.Kitchen.NotifyAsync(AvTransportEventPath, SonosEventBodies.AvTransport(("CurrentTrackURI", "x-file-cifs://nas/music/song-b.mp3")));
        Assert.Null(household.KitchenPlayer.CurrentTrackPosition);
        household.ClearCalls();

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(TimeSpan.Zero, household.KitchenPlayer.CurrentTrackPosition);
        Assert.Equal(["GetPositionInfo", "GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action).Order());
    }

    [Fact]
    public async Task WhenAnEventCannotBeApplied_ThenThePlayerIsPolledFullyOnce()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        await household.Office.NotifyAsync(RenderingControlEventPath, SonosEventBodies.Properties(("LastChange", "<Event><InstanceID")));
        household.ClearCalls();

        // Act
        await household.PollAsync();
        var callsAfterBrokenEvent = household.Office.Calls.Count;
        household.ClearCalls();
        await household.PollAsync();

        // Assert
        Assert.Equal(PlayerPollCalls, callsAfterBrokenEvent);
        Assert.Empty(household.Office.Calls);
    }

    [Theory]
    [InlineData(new uint[] { 0, 1, 2 }, false)]
    [InlineData(new uint[] { 0, 2 }, true)]
    [InlineData(new uint[] { 0, 1, 0 }, true)]
    [InlineData(new uint[] { 3 }, true)]
    public async Task WhenTheEventSequenceHasAGap_ThenThePlayerIsPolledFully(uint[] sequences, bool isPolledFully)
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        foreach (var sequence in sequences)
        {
            await household.Office.NotifyAsync(RenderingControlEventPath, SonosEventBodies.Properties(), sequence);
        }

        household.ClearCalls();

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(isPolledFully ? PlayerPollCalls : 0, household.Office.Calls.Count);
    }

    [Fact]
    public async Task WhenAPlayerRebooted_ThenItIsPolledFullyAndSubscribedAgain()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        var topology = SonosEventBodies.CreateStandaloneTopology(
            (TestFixtures.KitchenUuid, "Küche", household.Kitchen.BaseUri), (TestFixtures.OfficeUuid, "Büro", household.Office.BaseUri));
        string WithOfficeBootSequence(string bootSequence) => SonosEventBodies.Properties(("ZoneGroupState",
            topology.Replace($"UUID=\"{TestFixtures.OfficeUuid}\"", $"UUID=\"{TestFixtures.OfficeUuid}\" BootSeq=\"{bootSequence}\"", StringComparison.Ordinal)));
        await household.Kitchen.NotifyAsync(TopologyEventPath, WithOfficeBootSequence("7"));
        await household.PollAsync();

        // Act
        await household.Kitchen.NotifyAsync(TopologyEventPath, WithOfficeBootSequence("8"));
        household.ClearCalls();
        await household.PollAsync();

        // Assert
        Assert.Equal(PlayerPollCalls, household.Office.Calls.Count);
        Assert.Equal(2, household.Office.Subscribed.Count(path => path == AvTransportEventPath));
        Assert.Equal(1, household.Kitchen.Subscribed.Count(path => path == TopologyEventPath));
        Assert.Equal(["GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action));
    }

    [Fact]
    public async Task WhenTheRebootedSeedStartsItsNewTopologySubscriptionWithPartOfTheHousehold_ThenNoPlayerIsTakenOffline()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        var kitchenMember = (TestFixtures.KitchenUuid, "Küche", household.Kitchen.BaseUri);
        var allRooms = SonosEventBodies.CreateStandaloneTopology(kitchenMember, (TestFixtures.OfficeUuid, "Büro", household.Office.BaseUri));
        var kitchenOnly = SonosEventBodies.CreateStandaloneTopology(kitchenMember);
        string WithKitchenBootSequence(string bootSequence) => SonosEventBodies.Properties(("ZoneGroupState",
            allRooms.Replace($"UUID=\"{TestFixtures.KitchenUuid}\"", $"UUID=\"{TestFixtures.KitchenUuid}\" BootSeq=\"{bootSequence}\"", StringComparison.Ordinal)));
        await household.Kitchen.NotifyAsync(TopologyEventPath, WithKitchenBootSequence("1"));
        await household.PollAsync();

        // The seed rebooted: it reports a new boot sequence, then part of the household to the poll and to the initial
        // event of the topology subscription made again for its reboot.
        await household.Kitchen.NotifyAsync(TopologyEventPath, WithKitchenBootSequence("2"));
        household.Kitchen.RespondWithTopology(kitchenMember);
        household.Kitchen.SendInitialEventBeforeAnswering(TopologyEventPath, SonosEventBodies.Properties(("ZoneGroupState", kitchenOnly)));

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(2, household.Kitchen.Subscribed.Count(path => path == TopologyEventPath));
        Assert.True(household.OfficePlayer.IsConnected);
    }

    [Fact]
    public async Task WhenAnEventReplacesPartOfAPollThatRepairsMissedEvents_ThenTheNextPollIsFullAgain()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        await household.Office.NotifyAsync(RenderingControlEventPath, SonosEventBodies.Properties(), sequence: 3);
        await household.ReplaceVolumeDuringPollAsync();
        household.ClearCalls();

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(PlayerPollCalls, household.Office.Calls.Count);
    }

    [Fact]
    public async Task WhenAnEventReplacesPartOfTheFullReconciliationAfterAFailedPoll_ThenThePlayerIsPolledFullyAgain()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        household.Kitchen.RespondWithServerError("GetZoneGroupState");
        await Assert.ThrowsAnyAsync<Exception>(() => household.PollAsync());
        household.Kitchen.ClearServerError("GetZoneGroupState");
        await household.ReplaceVolumeDuringPollAsync();
        household.ClearCalls();

        // Act
        await household.PollAsync();

        // Assert
        Assert.Equal(PlayerPollCalls, household.Office.Calls.Count);
        Assert.Equal(["GetZoneGroupState"], household.Kitchen.Calls.Select(call => call.Action));
    }

    [Fact]
    public async Task WhenAReadStartsToFaultDuringAPollThatRepairsMissedEvents_ThenThePlayerIsPolledFullyOnceMore()
    {
        // Arrange
        await using var household = await PolledHousehold.StartAsync(sendsEvents: true);
        await household.Office.NotifyAsync(RenderingControlEventPath, SonosEventBodies.Properties(), sequence: 3);
        household.Office.RespondWithFault("GetVolume", 501);
        await household.PollAsync();
        household.ClearCalls();

        // Act
        await household.PollAsync();
        var callsAfterFault = household.Office.Calls.Count;
        household.ClearCalls();
        await household.PollAsync();

        // Assert
        Assert.Equal(PlayerPollCalls, callsAfterFault);
        Assert.Empty(household.Office.Calls);
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

                    // Reads the position once for each coordinator, which the AVTransport events just sent ask for.
                    await household.PollAsync();
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

        /// <summary>
        /// Runs a poll during which the office sends a volume event while its volume read is held, so the event
        /// replaces the rendering control values of a full poll of the office.
        /// </summary>
        internal async Task ReplaceVolumeDuringPollAsync()
        {
            Office.ClearCalls();
            var hold = Office.HoldAction("GetVolume");
            var poll = PollAsync();
            await AsyncTestHelpers.WaitUntilAsync(
                () => Office.Calls.Any(call => call.Action == "GetVolume"),
                ConnectedSystem.WaitTimeout,
                message: "The poll should read the volume of the office.");
            await Office.NotifyAsync(RenderingControlEventPath, SonosEventBodies.RenderingControl(("Volume", "Master", "20")));
            hold.SetResult();
            await poll;
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
