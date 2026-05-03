using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.Shelly.Model;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Shelly;

[InterceptorSubject]
public partial class ShellyEnergyMeter :
    IPowerMeter,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    [State(Unit = StateUnit.Watt)]
    public partial decimal? MeasuredPower { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    public partial decimal? MeasuredEnergyConsumed { get; internal set; }

    [State(Unit = StateUnit.Watt, Position = 312)]
    public partial decimal? TotalActivePower { get; internal set; }

    [State(Unit = StateUnit.Watt, Position = 313)]
    public partial decimal? TotalApparentPower { get; internal set; }

    [State(Unit = StateUnit.Ampere, Position = 314)]
    public partial decimal? TotalCurrent { get; internal set; }

    [State(Unit = StateUnit.Ampere, Position = 315)]
    public partial decimal? NeutralCurrent { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 316)]
    public partial decimal? TotalReturnedEnergy { get; internal set; }

    [State]
    public partial ShellyEnergyMeterPhase[] Phases { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => "Energy Meter";

    [Derived]
    public string IconName => "ElectricMeter";

    public string? IconColor => null;

    public ShellyEnergyMeter()
    {
        MeasuredPower = null;
        MeasuredEnergyConsumed = null;
        TotalActivePower = null;
        TotalApparentPower = null;
        TotalCurrent = null;
        NeutralCurrent = null;
        TotalReturnedEnergy = null;
        LastUpdated = null;
        Phases =
        [
            new ShellyEnergyMeterPhase("a"),
            new ShellyEnergyMeterPhase("b"),
            new ShellyEnergyMeterPhase("c")
        ];
    }

    internal void UpdateFromStatus(ShellyEmStatus status)
    {
        TotalActivePower = status.TotalActivePower;
        TotalApparentPower = status.TotalApparentPower;
        TotalCurrent = status.TotalCurrent;
        NeutralCurrent = status.NeutralCurrent;
        MeasuredPower = status.TotalActivePower;

        Phases[0].ElectricalVoltage = status.PhaseAVoltage;
        Phases[0].ElectricalCurrent = status.PhaseACurrent;
        Phases[0].ElectricalFrequency = status.PhaseAFrequency;
        Phases[0].ActivePower = status.PhaseAActivePower;
        Phases[0].ApparentPower = status.PhaseAApparentPower;
        Phases[0].PowerFactor = status.PhaseAPowerFactor;

        Phases[1].ElectricalVoltage = status.PhaseBVoltage;
        Phases[1].ElectricalCurrent = status.PhaseBCurrent;
        Phases[1].ElectricalFrequency = status.PhaseBFrequency;
        Phases[1].ActivePower = status.PhaseBActivePower;
        Phases[1].ApparentPower = status.PhaseBApparentPower;
        Phases[1].PowerFactor = status.PhaseBPowerFactor;

        Phases[2].ElectricalVoltage = status.PhaseCVoltage;
        Phases[2].ElectricalCurrent = status.PhaseCCurrent;
        Phases[2].ElectricalFrequency = status.PhaseCFrequency;
        Phases[2].ActivePower = status.PhaseCActivePower;
        Phases[2].ApparentPower = status.PhaseCApparentPower;
        Phases[2].PowerFactor = status.PhaseCPowerFactor;

        LastUpdated = DateTimeOffset.UtcNow;
        Phases[0].LastUpdated = LastUpdated;
        Phases[1].LastUpdated = LastUpdated;
        Phases[2].LastUpdated = LastUpdated;
    }

    internal void UpdateFromDataStatus(ShellyEmDataStatus dataStatus)
    {
        MeasuredEnergyConsumed = dataStatus.TotalActiveEnergy;
        TotalReturnedEnergy = dataStatus.TotalActiveReturnedEnergy;

        Phases[0].TotalActiveEnergy = dataStatus.PhaseATotalActiveEnergy;
        Phases[0].TotalReturnedEnergy = dataStatus.PhaseATotalReturnedEnergy;
        Phases[1].TotalActiveEnergy = dataStatus.PhaseBTotalActiveEnergy;
        Phases[1].TotalReturnedEnergy = dataStatus.PhaseBTotalReturnedEnergy;
        Phases[2].TotalActiveEnergy = dataStatus.PhaseCTotalActiveEnergy;
        Phases[2].TotalReturnedEnergy = dataStatus.PhaseCTotalReturnedEnergy;
    }
}
