using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using HueApi.Models;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Philips Hue button device (e.g., dimmer switch, tap switch) with battery and multiple buttons.
/// </summary>
[InterceptorSubject]
public partial class HueButtonDevice : HueDevice,
    IBatteryState
{
    internal DevicePower? DevicePowerResource { get; set; }

    [Derived]
    public override string? IconName =>
        Buttons.Any(button => button.ButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.None)
            ? "RadioButtonChecked"
            : "RadioButtonUnchecked";

    [Derived]
    public override string? IconColor => IsConnected ? "Default" : "Error";

    [Derived]
    [State]
    public decimal? BatteryLevel => DevicePowerResource?.PowerState?.BatteryLevel / 100m;

    [State]
    public partial HueButton[] Buttons { get; set; }

    public HueButtonDevice(
        Device device,
        ZigbeeConnectivity? zigbeeConnectivity,
        DevicePower? devicePower,
        ButtonResource[] buttons,
        HueBridge bridge)
        : base(device, zigbeeConnectivity, bridge)
    {
        DevicePowerResource = devicePower;
        Buttons = [];
        Update(device, zigbeeConnectivity, devicePower, buttons);
    }

    internal HueButtonDevice Update(
        Device device,
        ZigbeeConnectivity? zigbeeConnectivity,
        DevicePower? devicePower,
        ButtonResource[] buttons)
    {
        Update(device, zigbeeConnectivity);
        DevicePowerResource = devicePower;

        Buttons = buttons
            .Select((button, index) => Buttons
                .SingleOrDefault(existingButton => existingButton.ResourceId == button.Id)?
                    .Update(button, Buttons.Length == 0)
                ?? new HueButton("Button " + (index + 1), button, this, Buttons.Length == 0))
            .ToArray();

        return this;
    }
}
