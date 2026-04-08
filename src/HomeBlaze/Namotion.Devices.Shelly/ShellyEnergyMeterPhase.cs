using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Shelly;

[InterceptorSubject]
public partial class ShellyEnergyMeterPhase :
    IElectricalVoltageSensor,
    IElectricalCurrentSensor,
    IElectricalFrequencySensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    internal string Phase { get; }

    [State(Unit = StateUnit.Volt)]
    public partial decimal? ElectricalVoltage { get; internal set; }

    [State(Unit = StateUnit.Ampere)]
    public partial decimal? ElectricalCurrent { get; internal set; }

    [State(Unit = StateUnit.Hertz)]
    public partial decimal? ElectricalFrequency { get; internal set; }

    [State(Unit = StateUnit.Watt, Position = 312)]
    public partial decimal? ActivePower { get; internal set; }

    [State(Unit = StateUnit.Watt, Position = 313)]
    public partial decimal? ApparentPower { get; internal set; }

    [State(Position = 350)]
    public partial decimal? PowerFactor { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 316)]
    public partial decimal? TotalActiveEnergy { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 317)]
    public partial decimal? TotalReturnedEnergy { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => $"Phase {Phase.ToUpperInvariant()}";

    [Derived]
    public string IconName => "Bolt";

    public string? IconColor => null;

    public ShellyEnergyMeterPhase(string phase)
    {
        Phase = phase;
        ElectricalVoltage = null;
        ElectricalCurrent = null;
        ElectricalFrequency = null;
        ActivePower = null;
        ApparentPower = null;
        PowerFactor = null;
        TotalActiveEnergy = null;
        TotalReturnedEnergy = null;
        LastUpdated = null;
    }
}
