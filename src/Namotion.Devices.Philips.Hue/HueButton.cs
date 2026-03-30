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
    IButtonDevice,
    IObservable<HomeBlaze.Abstractions.Inputs.ButtonEvent>,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IDisposable
{
    private readonly string _name;
    private readonly Subject<HomeBlaze.Abstractions.Inputs.ButtonEvent> _buttonEventSubject = new();

    private ButtonState? _currentButtonState;
    private DateTimeOffset? _currentButtonChangeDate;

    internal ButtonResource ButtonResource { get; set; }

    public HueButtonDevice ParentDevice { get; }

    public Guid ResourceId => ButtonResource.Id;

    public string Id =>
        ParentDevice.Bridge.BridgeId + $"/devices/{ParentDevice.ResourceId}/buttons/{ResourceId}";

    public DateTimeOffset? ButtonChangeDate =>
        ButtonResource?.Button?.ButtonReport?.Updated;

    [State]
    public partial ButtonState? ButtonState { get; set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; set; }

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
            if (lastEvent != null && lastEvent.HasValue)
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

        return HomeBlaze.Abstractions.Inputs.ButtonState.None;
    }

    public HueButton(string name, ButtonResource buttonResource, HueButtonDevice buttonDevice, bool initialization)
    {
        _name = name;
        ButtonResource = buttonResource;
        ParentDevice = buttonDevice;
        ButtonState = HomeBlaze.Abstractions.Inputs.ButtonState.None;

        _currentButtonChangeDate = ButtonChangeDate;
        _currentButtonState = InternalButtonState;

        if (!initialization)
        {
            RefreshButtonState();
        }
    }

    internal HueButton Update(ButtonResource buttonResource, bool initialization)
    {
        ButtonResource = buttonResource;
        LastUpdated = DateTimeOffset.Now;

        if (!initialization)
        {
            RefreshButtonState();
        }

        return this;
    }

    public void RefreshButtonState()
    {
        var newButtonChangeDate = ButtonChangeDate;
        var newButtonState = InternalButtonState;

        if (newButtonChangeDate != null &&
            newButtonChangeDate != _currentButtonChangeDate &&
            newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.None &&
            newButtonState != _currentButtonState)
        {
            _currentButtonChangeDate = newButtonChangeDate;
            _currentButtonState = newButtonState;

            if (newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.Repeat ||
                ButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.Repeat)
            {
                ButtonState = newButtonState;

                if (newButtonState.HasValue)
                {
                    _buttonEventSubject.OnNext(new HomeBlaze.Abstractions.Inputs.ButtonEvent
                    {
                        Button = this,
                        ButtonState = newButtonState.Value,
                        Timestamp = DateTimeOffset.UtcNow,
                        DeviceId = Id
                    });
                }

                if (newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.None &&
                    newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.Down &&
                    newButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.Repeat)
                {
                    // Reset back to None after release events
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
