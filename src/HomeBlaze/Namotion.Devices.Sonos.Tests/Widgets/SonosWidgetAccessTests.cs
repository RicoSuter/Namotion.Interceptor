using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Devices.Sonos.HomeBlaze;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace Namotion.Devices.Sonos.Tests.Widgets;

/// <summary>
/// The widgets run the operations of the speakers only when the viewer may invoke them.
/// </summary>
public sealed class SonosWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private const string DeniedTooltip = "Your roles do not allow this.";

    private readonly TestUserPermissions _permissions = new();
    private readonly List<MessageBoxOptions> _shownMessages = [];
    private readonly FakeSonosSpeaker _speaker = new();
    private ConnectedSystem? _connected;
    private bool _isDialogFailing;

    public SonosWidgetAccessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);

        var dialogs = new Mock<IDialogService>();
        dialogs
            .Setup(service => service.ShowMessageBoxAsync(It.IsAny<MessageBoxOptions>(), It.IsAny<DialogOptions?>()))
            .ReturnsAsync((MessageBoxOptions options, DialogOptions? _) =>
            {
                _shownMessages.Add(options);
                return _isDialogFailing ? throw new ObjectDisposedException("circuit") : true;
            });
        Services.AddSingleton(dialogs.Object);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    private SonosPlayer Player => _connected!.Player;

    // The fake speaker describes itself as a soundbar.
    private SonosHomeTheater HomeTheater => Player.HomeTheater!;

    public static TheoryData<string, string> PlayerControls() => new()
    {
        { "sonos-previous", nameof(SonosPlayer.PreviousAsync) },
        { "sonos-toggle-playback", nameof(SonosPlayer.TogglePlaybackAsync) },
        { "sonos-next", nameof(SonosPlayer.NextAsync) },
        { "sonos-mute", nameof(SonosPlayer.MuteAsync) },
        { "sonos-volume", nameof(SonosPlayer.SetVolumeAsync) },
    };

    public static TheoryData<string, string> GroupControls() => new()
    {
        { "sonos-toggle-playback", nameof(SonosGroup.TogglePlaybackAsync) },
        { "sonos-volume", nameof(SonosGroup.SetVolumeAsync) },
    };

    public static TheoryData<string, string> HomeTheaterControls() => new()
    {
        { "sonos-night-mode", nameof(SonosHomeTheater.SetNightModeAsync) },
        { "sonos-speech-enhancement", nameof(SonosHomeTheater.SetSpeechEnhancementAsync) },
    };

    public async Task InitializeAsync()
    {
        _connected = await ConnectedSystem.StartAsync(_speaker, configure: AttachContext);
    }

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        if (_connected is not null)
        {
            await _connected.DisposeAsync();
        }

        await _speaker.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(PlayerControls))]
    public void WhenTheUserMayInvokeTheOperationOfAPlayerControl_ThenTheControlIsEnabled(string testId, string methodName)
    {
        // Arrange
        Assert.NotNull(Player.TryGetMethodMember(methodName));

        // Act
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));

        // Assert
        AssertEnabled(rendered, testId);
    }

    [Theory]
    [MemberData(nameof(PlayerControls))]
    public void WhenTheUserMayNotInvokeTheOperationOfAPlayerControl_ThenTheControlIsDisabledWithTheReason(string testId, string methodName)
    {
        // Arrange
        _permissions.CannotInvoke.Add(Player.TryGetMethodMember(methodName)!);

        // Act
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));

        // Assert
        AssertDisabledWithReason(rendered, testId);
    }

    [Fact]
    public async Task WhenTheUserMayInvokeNext_ThenTheButtonSkipsToTheNextTrack()
    {
        // Arrange
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));

        // Act
        await rendered.Find("[data-testid=sonos-next]").ClickAsync(new());

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => _speaker.Calls.Any(call => call.Action == "Next"), message: "The speaker should skip to the next track.");
        Assert.Empty(_shownMessages);
    }

    [Fact]
    public async Task WhenTheUserMayInvokeSetVolume_ThenTheSliderSetsTheVolume()
    {
        // Arrange
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));

        // Act
        await FindInput(rendered, "sonos-volume").ChangeAsync(new() { Value = "30" });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => _speaker.Calls.Any(call => call.Action == "SetVolume" && call.GetArgument("DesiredVolume") == "30"),
            message: "The speaker should set the volume.");
    }

    [Fact]
    public async Task WhenTheRolesChangeAfterTheButtonRendered_ThenNextDoesNotRunAndTheRefusalIsShown()
    {
        // Arrange
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));
        _permissions.CannotInvoke.Add(Player.TryGetMethodMember(nameof(SonosPlayer.NextAsync))!);

        // Act
        await rendered.Find("[data-testid=sonos-next]").ClickAsync(new());

        // Assert
        var message = Assert.Single(_shownMessages);
        Assert.Equal("Not allowed", message.Title);
        Assert.DoesNotContain(_speaker.Calls, call => call.Action == "Next");
    }

    [Fact]
    public void WhenTheRolesChange_ThenThePlayerWidgetRendersTheNewAnswer()
    {
        // Arrange
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));
        AssertEnabled(rendered, "sonos-next");

        // Act
        _permissions.CannotInvoke.Add(Player.TryGetMethodMember(nameof(SonosPlayer.NextAsync))!);
        _permissions.RaiseChanged();

        // Assert
        rendered.WaitForAssertion(() => AssertDisabledWithReason(rendered, "sonos-next"));
    }

    [Theory]
    [MemberData(nameof(HomeTheaterControls))]
    public void WhenTheUserMayNotInvokeTheOperationOfAHomeTheaterControl_ThenTheControlIsDisabledWithTheReason(string testId, string methodName)
    {
        // Arrange
        _permissions.CannotInvoke.Add(HomeTheater.TryGetMethodMember(methodName)!);

        // Act
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));

        // Assert
        AssertDisabledWithReason(rendered, testId);
    }

    [Fact]
    public async Task WhenTheUserMayInvokeSetNightMode_ThenTheSwitchTurnsNightModeOff()
    {
        // Arrange
        Assert.True(HomeTheater.NightMode);
        _speaker.RespondToEqualizer("NightMode", "0");
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));
        AssertEnabled(rendered, "sonos-night-mode");

        // Act
        await FindInput(rendered, "sonos-night-mode").ChangeAsync(new() { Value = false });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => _speaker.Calls.Any(call => call.Action == "SetEQ" && call.GetArgument("EQType") == "NightMode" && call.GetArgument("DesiredValue") == "0"),
            message: "The speaker should turn night mode off.");
    }

    [Theory]
    [MemberData(nameof(GroupControls))]
    public void WhenTheUserMayInvokeTheOperationOfAGroupControl_ThenTheControlIsEnabled(string testId, string methodName)
    {
        // Arrange
        var group = Assert.Single(_connected!.System.Groups).Value;
        Assert.NotNull(group.TryGetMethodMember(methodName));

        // Act
        var rendered = Render<SonosGroupWidget>(parameters => parameters.Add(widget => widget.Subject, group));

        // Assert
        AssertEnabled(rendered, testId);
    }

    [Theory]
    [MemberData(nameof(GroupControls))]
    public void WhenTheUserMayNotInvokeTheOperationOfAGroupControl_ThenTheControlIsDisabledWithTheReason(string testId, string methodName)
    {
        // Arrange
        var group = Assert.Single(_connected!.System.Groups).Value;
        _permissions.CannotInvoke.Add(group.TryGetMethodMember(methodName)!);

        // Act
        var rendered = Render<SonosGroupWidget>(parameters => parameters.Add(widget => widget.Subject, group));

        // Assert
        AssertDisabledWithReason(rendered, testId);
    }

    [Fact]
    public async Task WhenTheUserMayInvokeTheGroupVolume_ThenTheSliderSetsTheGroupVolume()
    {
        // Arrange
        var group = Assert.Single(_connected!.System.Groups).Value;
        var rendered = Render<SonosGroupWidget>(parameters => parameters.Add(widget => widget.Subject, group));

        // Act
        await FindInput(rendered, "sonos-volume").ChangeAsync(new() { Value = "30" });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => _speaker.Calls.Any(call => call.Action == "SetGroupVolume" && call.GetArgument("DesiredVolume") == "30"),
            message: "The speaker should set the group volume.");
    }

    [Fact]
    public async Task WhenTheRolesChangeAfterTheGroupSliderRendered_ThenTheGroupVolumeIsNotSetAndTheRefusalIsShown()
    {
        // Arrange
        var group = Assert.Single(_connected!.System.Groups).Value;
        var rendered = Render<SonosGroupWidget>(parameters => parameters.Add(widget => widget.Subject, group));
        _permissions.CannotInvoke.Add(group.TryGetMethodMember(nameof(SonosGroup.SetVolumeAsync))!);

        // Act
        await FindInput(rendered, "sonos-volume").ChangeAsync(new() { Value = "30" });

        // Assert
        var message = Assert.Single(_shownMessages);
        Assert.Equal("Not allowed", message.Title);
        Assert.DoesNotContain(_speaker.Calls, call => call.Action is "SetGroupVolume" or "SnapshotGroupVolume");
    }

    [Fact]
    public async Task WhenTheRefusalCannotBeShown_ThenTheClickDoesNotThrow()
    {
        // Arrange
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));
        _permissions.CannotInvoke.Add(Player.TryGetMethodMember(nameof(SonosPlayer.NextAsync))!);
        _isDialogFailing = true;

        // Act
        await rendered.Find("[data-testid=sonos-next]").ClickAsync(new());

        // Assert
        Assert.Single(_shownMessages);
        Assert.DoesNotContain(_speaker.Calls, call => call.Action == "Next");
    }

    [Fact]
    public async Task WhenThePlayerWidgetIsDisposed_ThenItNoLongerListensForChanges()
    {
        // Arrange
        Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));
        Assert.Equal(1, _permissions.ChangedSubscriberCount);

        // Act
        await DisposeComponentsAsync();

        // Assert
        Assert.Equal(0, _permissions.ChangedSubscriberCount);
    }

    [Fact]
    public async Task WhenTheGroupWidgetIsDisposed_ThenItNoLongerListensForChanges()
    {
        // Arrange
        var group = Assert.Single(_connected!.System.Groups).Value;
        Render<SonosGroupWidget>(parameters => parameters.Add(widget => widget.Subject, group));
        Assert.Equal(1, _permissions.ChangedSubscriberCount);

        // Act
        await DisposeComponentsAsync();

        // Assert
        Assert.Equal(0, _permissions.ChangedSubscriberCount);
    }

    [Fact]
    public void WhenAPlayerIsHidden_ThenTheSystemWidgetNeitherListsNorCountsIt()
    {
        // Arrange
        var system = _connected!.System;
        var shown = Render<SonosSystemWidget>(parameters => parameters.Add(widget => widget.Subject, system));
        var roomName = Player.RoomName;
        var shownMarkup = shown.Markup;
        _permissions.CannotShow.Add(Player);
        _permissions.CannotShow.Add(Assert.Single(system.Groups).Value);

        // Act
        var hidden = Render<SonosSystemWidget>(parameters => parameters.Add(widget => widget.Subject, system));

        // Assert
        Assert.Contains(roomName, shownMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain(roomName, hidden.Markup, StringComparison.Ordinal);
        Assert.Equal(["0", "0"], hidden.FindAll(".mud-typography-h5").Select(value => value.TextContent.Trim()));
    }

    [Fact]
    public void WhenAMemberOfAGroupIsHidden_ThenTheGroupWidgetDoesNotNameIt()
    {
        // Arrange
        var group = Assert.Single(_connected!.System.Groups).Value;
        var roomName = Player.RoomName;
        _permissions.CannotShow.Add(Player);

        // Act
        var rendered = Render<SonosGroupWidget>(parameters => parameters.Add(widget => widget.Subject, group));

        // Assert
        Assert.Contains(Player, group.Members);
        Assert.DoesNotContain(roomName, rendered.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheHomeTheaterIsHidden_ThenThePlayerWidgetDoesNotShowItsControls()
    {
        // Arrange
        _permissions.CannotShow.Add(HomeTheater);

        // Act
        var rendered = Render<SonosPlayerWidget>(parameters => parameters.Add(widget => widget.Subject, Player));

        // Assert
        Assert.Empty(rendered.FindAll("[data-testid=sonos-night-mode]"));
        Assert.Empty(rendered.FindAll("[data-testid=sonos-speech-enhancement]"));
    }

    [Fact]
    public void WhenAPlayerIsHidden_ThenTheEditComponentDoesNotCountIt()
    {
        // Arrange
        var system = _connected!.System;
        _permissions.CannotShow.Add(Player);

        // Act
        var rendered = Render<SonosSystemEditComponent>(parameters => parameters.Add(component => component.Subject, system));

        // Assert
        Assert.Contains("Rooms: 0", rendered.Markup, StringComparison.Ordinal);
    }

    private static void AttachContext(SonosSystem system)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);
        ((IInterceptorSubject)system).Context.AddFallbackContext(context);
    }

    private static void AssertEnabled<TComponent>(IRenderedComponent<TComponent> rendered, string testId)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        var control = rendered.Find($"[data-testid={testId}]");
        Assert.False(IsDisabled(control), rendered.Markup);
        Assert.Null(control.GetAttribute("title"));
    }

    private static void AssertDisabledWithReason<TComponent>(IRenderedComponent<TComponent> rendered, string testId)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        var control = rendered.Find($"[data-testid={testId}]");
        Assert.True(IsDisabled(control), rendered.Markup);
        Assert.Equal(DeniedTooltip, control.GetAttribute("title"));
    }

    // MudBlazor puts the attributes of a slider and a switch on their input or on an element around it.
    private static AngleSharp.Dom.IElement FindInput<TComponent>(IRenderedComponent<TComponent> rendered, string testId)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        var control = rendered.Find($"[data-testid={testId}]");
        return control.TagName == "INPUT" ? control : control.QuerySelector("input")!;
    }

    private static bool IsDisabled(AngleSharp.Dom.IElement control) =>
        control.HasAttribute("disabled") || control.QuerySelectorAll("[disabled]").Length > 0;
}
