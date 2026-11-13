using System.ComponentModel;
using System.Net;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// Night mode and speech enhancement live on a child subject that only home theater players have.
/// </summary>
public class SonosHomeTheaterTests
{
    private const string RenderingControlEventPath = "/MediaRenderer/RenderingControl/Event";

    private static readonly SonosDeviceDescription PlainSpeaker =
        new("Sonos One", "S18", new HashSet<string>(StringComparer.Ordinal) { "AVTransport", "RenderingControl" });

    private static SonosDeviceDescription Soundbar() =>
        DeviceDescriptionParser.Parse(Read("device-description-ray.xml"));

    private static SonosPlayer CreateSoundbar(SonosSystem? system = null)
    {
        var player = (system ?? CreateReachableHousehold()).Players[LivingRoomUuid];
        player.ApplyDescription(Soundbar());
        return player;
    }

    private static SonosPlayerReading Reading(bool? nightMode, bool? speechEnhancement) => new(
        new AvTransportChange(null, null, null, null, null, null),
        null,
        null,
        new RenderingControlChange(null, null, null, null, null, nightMode, speechEnhancement));

    [Fact]
    public void WhenPlayerWasNotDescribedYet_ThenItHasNoHomeTheater()
    {
        // Arrange
        var player = CreateHousehold().Players[LivingRoomUuid];

        // Act
        var homeTheater = player.HomeTheater;

        // Assert
        Assert.Null(homeTheater);
        Assert.False(player.IsHomeTheater);
    }

    [Fact]
    public void WhenDescriptionListsHomeTheaterControl_ThenThePlayerGetsAHomeTheater()
    {
        // Arrange
        var player = CreateHousehold().Players[LivingRoomUuid];

        // Act
        player.ApplyDescription(Soundbar());

        // Assert
        var homeTheater = Assert.IsType<SonosHomeTheater>(player.HomeTheater);
        Assert.True(player.IsHomeTheater);
        Assert.Equal("Home Theater (Wohnzimmer)", homeTheater.Title);
        Assert.Equal("Tv", homeTheater.IconName);
        Assert.Null(homeTheater.NightMode);
        Assert.Null(homeTheater.SpeechEnhancement);
    }

    [Fact]
    public void WhenDescriptionListsNoHomeTheaterControl_ThenThePlayerHasNoHomeTheater()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];

        // Act
        player.ApplyDescription(PlainSpeaker);

        // Assert
        Assert.Null(player.HomeTheater);
        Assert.False(player.IsHomeTheater);
        Assert.False(player.SwitchToTv_IsEnabled);
    }

    [Fact]
    public void WhenALaterDescriptionNoLongerListsHomeTheaterControl_ThenTheHomeTheaterIsRemoved()
    {
        // Arrange
        var player = CreateSoundbar();
        Assert.NotNull(player.HomeTheater);

        // Act
        player.ApplyDescription(PlainSpeaker);

        // Assert
        Assert.Null(player.HomeTheater);
        Assert.False(player.IsHomeTheater);
        Assert.False(player.SwitchToTv_IsEnabled);
    }

    [Fact]
    public void WhenPlayerIsDescribedAndPolledAgain_ThenTheSameHomeTheaterIsKept()
    {
        // Arrange
        var player = CreateSoundbar();
        var homeTheater = player.HomeTheater;
        player.ApplyPoll(Reading(nightMode: true, speechEnhancement: false), T0);

        // Act
        player.ApplyStaticData(Soundbar(), "serial", "mac", "hardware", "software");
        player.ApplyPoll(Reading(nightMode: true, speechEnhancement: true), T0 + 1);

        // Assert
        Assert.NotNull(homeTheater);
        Assert.Same(homeTheater, player.HomeTheater);
        Assert.True(homeTheater.NightMode);
        Assert.True(homeTheater.SpeechEnhancement);
    }

    [Fact]
    public void WhenRenderingControlEventReportsHomeTheaterSettings_ThenTheHomeTheaterUpdates()
    {
        // Arrange
        var player = CreateSoundbar();

        // Act
        player.ApplyRenderingControlEvent(new RenderingControlChange(null, null, null, null, null, NightMode: true, SpeechEnhancement: false), T0);

        // Assert
        Assert.True(player.HomeTheater!.NightMode);
        Assert.False(player.HomeTheater.SpeechEnhancement);
    }

    [Fact]
    public void WhenEventReportsOnlyNightMode_ThenSpeechEnhancementIsKept()
    {
        // Arrange
        var player = CreateSoundbar();
        player.ApplyRenderingControlEvent(new RenderingControlChange(null, null, null, null, null, NightMode: false, SpeechEnhancement: true), T0);

        // Act
        player.ApplyRenderingControlEvent(new RenderingControlChange(null, null, null, null, null, NightMode: true, SpeechEnhancement: null), T0 + 1);

        // Assert
        Assert.True(player.HomeTheater!.NightMode);
        Assert.True(player.HomeTheater.SpeechEnhancement);
    }

    [Fact]
    public void WhenPlayerIsNoHomeTheater_ThenNightModeOfAnEventIsIgnored()
    {
        // Arrange
        var player = CreateReachableHousehold().Players[KitchenUuid];
        player.ApplyDescription(PlainSpeaker);

        // Act
        player.ApplyRenderingControlEvent(new RenderingControlChange(30, null, null, null, null, NightMode: false, SpeechEnhancement: false), T0);

        // Assert
        Assert.Null(player.HomeTheater);
        Assert.Equal(0.3m, player.Volume);
    }

    [Fact]
    public void WhenPollStartedBeforeAnEvent_ThenTheHomeTheaterKeepsTheEventValues()
    {
        // Arrange
        var player = CreateSoundbar();
        player.ApplyRenderingControlEvent(new RenderingControlChange(null, null, null, null, null, NightMode: true, SpeechEnhancement: true), T0 + 1);

        // Act
        player.ApplyPoll(Reading(nightMode: false, speechEnhancement: false), T0);

        // Assert
        Assert.True(player.HomeTheater!.NightMode);
        Assert.True(player.HomeTheater.SpeechEnhancement);
    }

    [Fact]
    public void WhenOlderPollCompletesAfterNewerPoll_ThenTheHomeTheaterKeepsTheNewerValues()
    {
        // Arrange
        var player = CreateSoundbar();
        player.ApplyPoll(Reading(nightMode: true, speechEnhancement: true), T0 + 2);

        // Act
        player.ApplyPoll(Reading(nightMode: false, speechEnhancement: false), T0 + 1);

        // Assert
        Assert.True(player.HomeTheater!.NightMode);
        Assert.True(player.HomeTheater.SpeechEnhancement);
    }

    [Fact]
    public void WhenSystemIsNotConnected_ThenHomeTheaterOperationsAreDisabled()
    {
        // Arrange
        var homeTheater = CreateSoundbar().HomeTheater!;

        // Act
        var (isNightModeEnabled, isSpeechEnhancementEnabled) = (homeTheater.SetNightMode_IsEnabled, homeTheater.SetSpeechEnhancement_IsEnabled);

        // Assert
        Assert.False(isNightModeEnabled);
        Assert.False(isSpeechEnhancementEnabled);
    }

    [Fact]
    public void WhenPlayerStopsAnswering_ThenHomeTheaterOperationFlagsRaisePropertyChanged()
    {
        // Arrange
        var system = Track(CreateReachableHousehold());
        system.IsConnected = true;
        var player = Track(CreateSoundbar(system));
        var homeTheater = Track(player.HomeTheater!);
        Assert.True(homeTheater.SetNightMode_IsEnabled);
        Assert.True(homeTheater.SetSpeechEnhancement_IsEnabled);
        var firedEvents = new List<string>();
        ((INotifyPropertyChanged)homeTheater).PropertyChanged += (_, arguments) => firedEvents.Add(arguments.PropertyName!);

        // Act
        TakeOffline(player);

        // Assert
        Assert.False(homeTheater.SetNightMode_IsEnabled);
        Assert.False(homeTheater.SetSpeechEnhancement_IsEnabled);
        Assert.Contains(nameof(SonosHomeTheater.SetNightMode_IsEnabled), firedEvents);
        Assert.Contains(nameof(SonosHomeTheater.SetSpeechEnhancement_IsEnabled), firedEvents);
    }

    [Fact]
    public async Task WhenHomeTheaterSystemIsNotConnected_ThenSettingNightModeFails()
    {
        // Arrange
        var homeTheater = CreateSoundbar().HomeTheater!;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => homeTheater.SetNightModeAsync(true, CancellationToken.None));
    }

    [Fact]
    public async Task WhenSoundbarConnects_ThenItsHomeTheaterIsReadAndKeptAcrossPolls()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var homeTheater = connected.Player.HomeTheater;
        speaker.RespondToEqualizer("DialogLevel", "1");

        // Act
        await connected.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(homeTheater);
        Assert.Same(homeTheater, connected.Player.HomeTheater);
        Assert.True(homeTheater.NightMode);
        Assert.True(homeTheater.SpeechEnhancement);
        Assert.True(homeTheater.SetNightMode_IsEnabled);
    }

    [Fact]
    public async Task WhenSoundbarSendsANightModeEvent_ThenItsHomeTheaterUpdatesWithoutAPoll()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var homeTheater = connected.Player.HomeTheater!;
        Assert.True(homeTheater.NightMode);

        // Act
        var status = await speaker.NotifyAsync(RenderingControlEventPath, SonosEventBodies.RenderingControl(
            ("NightMode", null, "0"),
            ("DialogLevel", null, "1")));

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        await AsyncTestHelpers.WaitUntilAsync(
            () => homeTheater.NightMode == false && homeTheater.SpeechEnhancement == true,
            ConnectedSystem.WaitTimeout,
            message: "The RenderingControl event should reach the home theater of the player.");
    }

    [Fact]
    public async Task WhenNightModeIsSetOnTheHomeTheater_ThenTheSpeakerReceivesItAndTheStateIsReadBack()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var homeTheater = connected.Player.HomeTheater!;
        speaker.RespondToEqualizer("NightMode", "0");

        // Act
        await homeTheater.SetNightModeAsync(false, CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "SetEQ" && call.GetArgument("EQType") == "NightMode" && call.GetArgument("DesiredValue") == "0");
        Assert.False(homeTheater.NightMode);
    }

    private static T Track<T>(T subject) where T : IInterceptorSubject
    {
        subject.Context.AddFallbackContext(InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry());

        return subject;
    }
}
