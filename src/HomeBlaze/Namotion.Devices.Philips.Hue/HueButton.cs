using System.Reactive.Subjects;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Inputs;
using HueApi.Models;
using Namotion.Interceptor.Attributes;

using HueButtonEventType = HueApi.Models.ButtonEvent;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Individual button on a Philips Hue button device with reactive event stream.
/// </summary>
[InterceptorSubject]
public partial class HueButton :
    IMultiPressButtonDevice,
    IObservable<HomeBlaze.Abstractions.Inputs.ButtonEvent>,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IDisposable
{
    private readonly string _name;
    private readonly Subject<HomeBlaze.Abstractions.Inputs.ButtonEvent> _buttonEventSubject = new();

    // The event stream delivers each report as it happens, so one older than the last accepted by more
    // than this means the bridge clock moved back, such as after a reboot before its time sync. A poll
    // never counts as a clock jump: its snapshot carries the last report the bridge had, of any age.
    private static readonly TimeSpan ClockJumpThreshold = TimeSpan.FromMinutes(5);

    // The event stream and the poll apply reports concurrently. Guards ButtonResource, the last accepted
    // change date, the state writes and the emission.
    private readonly Lock _lock = new();

    private DateTimeOffset? _currentButtonChangeDate;

    internal ButtonResource ButtonResource { get; set; }

    public HueButtonDevice ParentDevice { get; }

    public Guid ResourceId => ButtonResource.Id;

    public string Id =>
        ParentDevice.Bridge.BridgeId + $"/devices/{ParentDevice.ResourceId}/buttons/{ResourceId}";

    public DateTimeOffset? ButtonChangeDate =>
        ButtonResource?.Button?.ButtonReport?.Updated;

    [State]
    public partial ButtonState? ButtonState { get; internal set; }

    [State]
    public partial int? PressCount { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => _name;

    [Derived]
    public string? IconName =>
        ButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.None
            ? "RadioButtonChecked"
            : "RadioButtonUnchecked";

    internal ButtonState? InternalButtonState
    {
        get
        {
            var lastEvent = ButtonResource?.Button?.ButtonReport?.Event;
            if (lastEvent is not null)
            {
                var eventType = lastEvent.Value;
                return GetButtonState(eventType);
            }

            return HomeBlaze.Abstractions.Inputs.ButtonState.None;
        }
    }

    public static ButtonState GetButtonState(HueButtonEventType eventType)
    {
        if (eventType == HueButtonEventType.initial_press)
        {
            return HomeBlaze.Abstractions.Inputs.ButtonState.Down;
        }
        else if (eventType == HueButtonEventType.repeat)
        {
            return HomeBlaze.Abstractions.Inputs.ButtonState.Repeat;
        }
        else if (eventType == HueButtonEventType.short_release)
        {
            return HomeBlaze.Abstractions.Inputs.ButtonState.Release;
        }
        else if (eventType == HueButtonEventType.long_release)
        {
            return HomeBlaze.Abstractions.Inputs.ButtonState.LongRelease;
        }
        else if (eventType == HueButtonEventType.double_short_release)
        {
            return HomeBlaze.Abstractions.Inputs.ButtonState.Release;
        }

        return HomeBlaze.Abstractions.Inputs.ButtonState.None;
    }

    /// <summary>
    /// Gets the number of presses of a sequence that ends with the event, or null for an event that ends no sequence of short presses.
    /// </summary>
    public static int? GetPressCount(HueButtonEventType eventType) => eventType switch
    {
        HueButtonEventType.short_release => 1,
        HueButtonEventType.double_short_release => 2,
        _ => null
    };

    public HueButton(string name, ButtonResource buttonResource, HueButtonDevice buttonDevice, bool initialization)
    {
        _name = name;
        ButtonResource = buttonResource;
        ParentDevice = buttonDevice;
        ButtonState = HomeBlaze.Abstractions.Inputs.ButtonState.None;
        PressCount = null;

        _currentButtonChangeDate = ButtonChangeDate;

        if (!initialization)
        {
            RefreshButtonState();
        }
    }

    internal HueButton Update(ButtonResource buttonResource, bool initialization)
    {
        lock (_lock)
        {
            ButtonResource = buttonResource;
            LastUpdated = DateTimeOffset.Now;

            if (!initialization)
            {
                RefreshButtonState(acceptClockJump: false);
            }
        }

        return this;
    }

    internal void ApplyEventStreamUpdate(Func<ButtonResource, ButtonResource> merge)
    {
        lock (_lock)
        {
            var previousButtonChangeDate = ButtonChangeDate;
            ButtonResource = merge(ButtonResource);
            LastUpdated = DateTimeOffset.Now;

            // Only an update that carries a new report is one the bridge just sent
            RefreshButtonState(acceptClockJump: ButtonChangeDate != previousButtonChangeDate);
        }
    }

    /// <summary>
    /// Emits the current button report unless it is not newer than the last emitted one.
    /// </summary>
    public void RefreshButtonState()
    {
        lock (_lock)
        {
            RefreshButtonState(acceptClockJump: false);
        }
    }

    // Called under _lock. The event is emitted under it as well: emitted after the lock is released,
    // the event of a later report could overtake the one of an earlier report.
    private void RefreshButtonState(bool acceptClockJump)
    {
        var newButtonChangeDate = ButtonChangeDate;
        var newButtonState = InternalButtonState;
        var pressCount = ButtonResource?.Button?.ButtonReport?.Event is { } eventType ? GetPressCount(eventType) : null;

        if (newButtonChangeDate != null &&
            (_currentButtonChangeDate is null ||
             newButtonChangeDate > _currentButtonChangeDate ||
             (acceptClockJump && _currentButtonChangeDate - newButtonChangeDate > ClockJumpThreshold)) &&
            newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.None)
        {
            _currentButtonChangeDate = newButtonChangeDate;

            // A release that follows a release is a press whose initial_press the bridge dropped, while
            // a held button keeps reporting repeat
            if (newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.Repeat ||
                ButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.Repeat)
            {
                // The count is set before the state, so that a reader of the Release state sees its count
                PressCount = pressCount;
                ButtonState = newButtonState;

                if (newButtonState.HasValue)
                {
                    _buttonEventSubject.OnNext(new HomeBlaze.Abstractions.Inputs.ButtonEvent
                    {
                        Button = this,
                        ButtonState = newButtonState.Value,
                        Timestamp = DateTimeOffset.UtcNow,
                        DeviceId = Id,
                        PressCount = pressCount ?? 1
                    });
                }

                if (newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.None &&
                    newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.Down &&
                    newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.Repeat)
                {
                    // Reset back to None after release events
                    PressCount = null;
                    ButtonState = HomeBlaze.Abstractions.Inputs.ButtonState.None;
                }
            }
        }
    }

    public IDisposable Subscribe(IObserver<HomeBlaze.Abstractions.Inputs.ButtonEvent> observer)
    {
        return _buttonEventSubject.Subscribe(observer);
    }

    public void Dispose()
    {
        _buttonEventSubject.OnCompleted();
        _buttonEventSubject.Dispose();
    }
}
