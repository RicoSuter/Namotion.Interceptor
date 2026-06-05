using System.Reactive.Subjects;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Shelly;

[InterceptorSubject]
public partial class ShellySwitch :
    IPowerRelay,
    IPowerMeter,
    IElectricalVoltageSensor,
    IElectricalCurrentSensor,
    ITemperatureSensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IObservable<SwitchEvent>,
    IDisposable
{
    private readonly Subject<SwitchEvent> _switchEventSubject = new();
    private readonly ShellyDevice _device;

    internal int Index { get; }

    [State(IsDiscrete = true)]
    public partial bool? IsOn { get; internal set; }

    [State(Position = 400)]
    public partial string? Source { get; internal set; }

    [State(Unit = StateUnit.Watt)]
    public partial decimal? MeasuredPower { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    public partial decimal? MeasuredEnergyConsumed { get; internal set; }

    [State(Unit = StateUnit.Volt)]
    public partial decimal? ElectricalVoltage { get; internal set; }

    [State(Unit = StateUnit.Ampere)]
    public partial decimal? ElectricalCurrent { get; internal set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => $"Switch {Index}";

    [Derived]
    public string IconName => IsOn == true ? "ToggleOn" : "ToggleOff";

    [Derived]
    public string? IconColor => IsOn == true ? "Success" : null;

    [Derived]
    [PropertyAttribute("TurnOn", KnownAttributes.IsEnabled)]
    public bool TurnOn_IsEnabled => _device.IsConnected && IsOn != true;

    [Derived]
    [PropertyAttribute("TurnOff", KnownAttributes.IsEnabled)]
    public bool TurnOff_IsEnabled => _device.IsConnected && IsOn == true;

    public ShellySwitch(ShellyDevice device, int index)
    {
        _device = device;
        Index = index;
        IsOn = null;
        Source = null;
        MeasuredPower = null;
        MeasuredEnergyConsumed = null;
        ElectricalVoltage = null;
        ElectricalCurrent = null;
        Temperature = null;
        LastUpdated = null;
    }

    [Operation(Title = "Turn On", Icon = "PowerSettingsNew", Position = 1)]
    public async Task TurnOnAsync(CancellationToken cancellationToken)
    {
        using var client = _device.CreateHttpClient();
        using var response = await client.GetAsync($"http://{_device.HostAddress}/rpc/Switch.Set?id={Index}&on=true", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    [Operation(Title = "Turn Off", Icon = "PowerOff", Position = 2)]
    public async Task TurnOffAsync(CancellationToken cancellationToken)
    {
        using var client = _device.CreateHttpClient();
        using var response = await client.GetAsync($"http://{_device.HostAddress}/rpc/Switch.Set?id={Index}&on=false", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    internal void PublishSwitchEvent()
    {
        _switchEventSubject.OnNext(new SwitchEvent
        {
            Switch = this,
            IsOn = IsOn == true,
            DeviceId = $"shelly-switch-{_device.HostAddress}-{Index}",
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public IDisposable Subscribe(IObserver<SwitchEvent> observer) =>
        _switchEventSubject.Subscribe(observer);

    public void Dispose()
    {
        _switchEventSubject.Dispose();
    }
}
