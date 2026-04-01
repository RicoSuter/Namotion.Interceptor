using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Covers;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Shelly;

[InterceptorSubject]
public partial class ShellyCover :
    IRollerShutter,
    IPowerMeter,
    IElectricalVoltageSensor,
    IElectricalCurrentSensor,
    IElectricalFrequencySensor,
    ITemperatureSensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    private readonly ShellyDevice _device;

    internal int Index { get; }

    [State(Unit = StateUnit.Watt)]
    public partial decimal? MeasuredPower { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    public partial decimal? MeasuredEnergyConsumed { get; internal set; }

    [State(Unit = StateUnit.Volt)]
    public partial decimal? ElectricalVoltage { get; internal set; }

    [State(Unit = StateUnit.Ampere)]
    public partial decimal? ElectricalCurrent { get; internal set; }

    [State(Unit = StateUnit.Hertz)]
    public partial decimal? ElectricalFrequency { get; internal set; }

    [State]
    public partial decimal? PowerFactor { get; internal set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State]
    public partial string? Source { get; internal set; }

    [State]
    public partial string? LastDirection { get; internal set; }

    /// <summary>
    /// Raw position from the API: 0 = fully closed, 100 = fully open.
    /// </summary>
    public partial int? CurrentPosition { get; internal set; }

    /// <summary>
    /// Raw state string from the API: "open", "closed", "opening", "closing", "stopped", "calibrating".
    /// </summary>
    public partial string? ApiState { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool? IsCalibrating { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    [State(Unit = StateUnit.Percent)]
    public decimal? Position => (100 - CurrentPosition) / 100m;

    [Derived]
    [State(IsDiscrete = true)]
    public RollerShutterState? ShutterState => ApiState switch
    {
        "opening" => RollerShutterState.Opening,
        "closing" => RollerShutterState.Closing,
        "stopped" or "closed" or "open" =>
            IsCalibrating == true ? RollerShutterState.Calibrating :
            Position == 0m ? RollerShutterState.Open :
            Position == 1m ? RollerShutterState.Closed :
            Position != null ? RollerShutterState.PartiallyOpen :
            RollerShutterState.Unknown,
        "calibrating" => RollerShutterState.Calibrating,
        _ => RollerShutterState.Unknown
    };

    [Derived]
    [State(IsDiscrete = true)]
    public bool? IsMoving => MeasuredPower != null ? MeasuredPower > 1 : null;

    [Derived]
    public string Title => $"Cover {Index}";

    [Derived]
    public string IconName => IsMoving == true ? "BlindsClosed" : "Blinds";

    [Derived]
    public string? IconColor => IsMoving == true ? "Warning" : null;

    public ShellyCover(ShellyDevice device, int index)
    {
        _device = device;
        Index = index;
        MeasuredPower = null;
        MeasuredEnergyConsumed = null;
        ElectricalVoltage = null;
        ElectricalCurrent = null;
        ElectricalFrequency = null;
        PowerFactor = null;
        Temperature = null;
        Source = null;
        LastDirection = null;
        CurrentPosition = null;
        ApiState = null;
        IsCalibrating = null;
        LastUpdated = null;
    }

    [Operation(Title = "Open", Icon = "KeyboardArrowUp", Position = 1)]
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        using var client = _device.CreateHttpClient();
        using var response = await client.GetAsync($"http://{_device.HostAddress}/rpc/Cover.Open?id={Index}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    [Operation(Title = "Close", Icon = "KeyboardArrowDown", Position = 2)]
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        using var client = _device.CreateHttpClient();
        using var response = await client.GetAsync($"http://{_device.HostAddress}/rpc/Cover.Close?id={Index}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    [Operation(Title = "Stop", Icon = "Stop", Position = 3)]
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var client = _device.CreateHttpClient();
        using var response = await client.GetAsync($"http://{_device.HostAddress}/rpc/Cover.Stop?id={Index}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    [Operation]
    public async Task SetPositionAsync(
        [OperationParameter(Unit = StateUnit.Percent)] decimal position,
        CancellationToken cancellationToken)
    {
        // Shelly API expects 0=closed, 100=open; HomeBlaze Position is 0=open, 1=closed
        var apiPosition = (int)((1m - Math.Clamp(position, 0m, 1m)) * 100);
        using var client = _device.CreateHttpClient();
        using var response = await client.GetAsync($"http://{_device.HostAddress}/rpc/Cover.GoToPosition?id={Index}&pos={apiPosition}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
