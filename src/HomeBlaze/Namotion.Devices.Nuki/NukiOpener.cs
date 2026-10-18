using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Model;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Nuki;

/// <summary>
/// A Nuki Opener paired with a bridge, which opens the door of an intercom system.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class NukiOpener : NukiDevice
{
    private const int ContinuousMode = 3;

    internal NukiOpener(NukiBridge bridge, long nukiId)
        : base(bridge, nukiId, NukiDeviceType.Opener)
    {
        OpenerState = null;
        IsContinuousModeActive = null;
        IsRinging = null;
        LastRingTime = null;
    }

    /// <summary>The state of the Opener, or null when it is unknown.</summary>
    [State(Position = 130, IsDiscrete = true)]
    public partial NukiOpenerState? OpenerState { get; internal set; }

    /// <summary>Whether every ring opens the door.</summary>
    [State(Position = 131, IsDiscrete = true)]
    public partial bool? IsContinuousModeActive { get; internal set; }

    /// <summary>Whether someone rang within the last 30 seconds.</summary>
    [State(Position = 132, IsDiscrete = true)]
    public partial bool? IsRinging { get; internal set; }

    /// <summary>When someone last rang.</summary>
    [State(Position = 133)]
    public partial DateTimeOffset? LastRingTime { get; internal set; }

    [Derived]
    public override string? IconName => IsRinging == true ? "NotificationsActive" : "Notifications";

    [Derived]
    public override string? IconColor => IsRinging == true ? "Warning" : null;

    [Derived]
    [PropertyAttribute("OpenDoor", KnownAttributes.IsEnabled)]
    public bool OpenDoor_IsEnabled => IsConnected && OpenerState is not NukiOpenerState.Opening;

    [Derived]
    [PropertyAttribute("ActivateRingToOpen", KnownAttributes.IsEnabled)]
    public bool ActivateRingToOpen_IsEnabled => IsConnected && OpenerState is NukiOpenerState.Online;

    [Derived]
    [PropertyAttribute("DeactivateRingToOpen", KnownAttributes.IsEnabled)]
    public bool DeactivateRingToOpen_IsEnabled => IsConnected && OpenerState is NukiOpenerState.RingToOpenActive;

    [Derived]
    [PropertyAttribute("ActivateContinuousMode", KnownAttributes.IsEnabled)]
    public bool ActivateContinuousMode_IsEnabled => IsConnected && IsContinuousModeActive == false;

    [Derived]
    [PropertyAttribute("DeactivateContinuousMode", KnownAttributes.IsEnabled)]
    public bool DeactivateContinuousMode_IsEnabled => IsConnected && IsContinuousModeActive == true;

    [Operation(Title = "Open door", Description = "Actuates the electric strike so that the door can be pushed open.", Icon = "MeetingRoom", RequiresConfirmation = true, Position = 1)]
    public Task OpenDoorAsync(CancellationToken cancellationToken) =>
        RunOpenerActionAsync(NukiOpenerAction.ElectricStrikeActuation, NukiOpenerState.Opening, "The Opener could not open the door.", cancellationToken);

    [Operation(Title = "Activate ring to open", Description = "Opens the door the next time someone rings.", RequiresConfirmation = true, Position = 2)]
    public Task ActivateRingToOpenAsync(CancellationToken cancellationToken) =>
        RunOpenerActionAsync(NukiOpenerAction.ActivateRingToOpen, null, "The Opener could not activate ring to open.", cancellationToken);

    [Operation(Title = "Deactivate ring to open", Description = "Stops opening the door on the next ring.", Position = 3)]
    public Task DeactivateRingToOpenAsync(CancellationToken cancellationToken) =>
        RunOpenerActionAsync(NukiOpenerAction.DeactivateRingToOpen, null, "The Opener could not deactivate ring to open.", cancellationToken);

    [Operation(Title = "Activate continuous mode", Description = "Opens the door every time someone rings, until continuous mode is deactivated.", RequiresConfirmation = true, Position = 4)]
    public Task ActivateContinuousModeAsync(CancellationToken cancellationToken) =>
        RunOpenerActionAsync(NukiOpenerAction.ActivateContinuousMode, null, "The Opener could not activate continuous mode.", cancellationToken);

    [Operation(Title = "Deactivate continuous mode", Description = "Stops opening the door on every ring.", Position = 5)]
    public Task DeactivateContinuousModeAsync(CancellationToken cancellationToken) =>
        RunOpenerActionAsync(NukiOpenerAction.DeactivateContinuousMode, null, "The Opener could not deactivate continuous mode.", cancellationToken);

    private protected override void UpdateState(NukiLastKnownStateResponse? state)
    {
        OpenerState = NukiStateMapping.ToOpenerState(state?.State);
        IsContinuousModeActive = state?.Mode is { } mode ? mode == ContinuousMode : null;
        IsRinging = state?.RingActionState;
        LastRingTime = state?.RingActionTimestamp;
    }

    private Task RunOpenerActionAsync(NukiOpenerAction action, NukiOpenerState? transition, string failureMessage, CancellationToken cancellationToken) =>
        RunActionAsync(
            (int)action,
            transition is { } state ? () => OpenerState = state : null,
            () => OpenerState = null,
            failureMessage,
            cancellationToken);
}
