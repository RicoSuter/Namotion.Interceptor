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

    /// <inheritdoc />
    /// <remarks>
    /// The value of the phase netted energy script (<c>Scripts/phase-netted-energy.js</c>) when it is installed, otherwise
    /// <see cref="TotalImportedPhaseEnergy"/>, which matches a billing meter only while no phases flow in opposite directions.
    /// See <see cref="IsTotalEnergyPhaseNetted"/>.
    /// </remarks>
    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    public partial decimal? TotalImportedEnergy { get; internal set; }

    /// <inheritdoc />
    /// <remarks>
    /// The value of the phase netted energy script (<c>Scripts/phase-netted-energy.js</c>) when it is installed, otherwise
    /// <see cref="TotalExportedPhaseEnergy"/>, which matches a billing meter only while no phases flow in opposite directions.
    /// See <see cref="IsTotalEnergyPhaseNetted"/>.
    /// </remarks>
    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    public partial decimal? TotalExportedEnergy { get; internal set; }

    [State(Unit = StateUnit.VoltAmpere, Position = 313)]
    public partial decimal? ApparentPower { get; internal set; }

    [State(Unit = StateUnit.Ampere, Position = 314)]
    public partial decimal? ElectricalCurrent { get; internal set; }

    [State(Unit = StateUnit.Ampere, Position = 315)]
    public partial decimal? NeutralCurrent { get; internal set; }

    /// <summary>
    /// Gets the sum of the per-phase import counters (device lifetime). The phases are split by direction before
    /// they are summed, so the value is higher than a billing meter's whenever phases flow in opposite directions.
    /// </summary>
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 316)]
    public partial decimal? TotalImportedPhaseEnergy { get; internal set; }

    /// <summary>
    /// Gets the sum of the per-phase export counters (device lifetime), split by direction before summing like
    /// <see cref="TotalImportedPhaseEnergy"/>.
    /// </summary>
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 317)]
    public partial decimal? TotalExportedPhaseEnergy { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether <see cref="TotalImportedEnergy"/> and <see cref="TotalExportedEnergy"/> are phase netted,
    /// meaning the phases are summed (vectorially, like a billing meter) before the energy is split into import and export:
    /// <c>true</c> while they come from the phase netted energy script (a counter can be <c>null</c> when HomeBlaze connects while the
    /// device reboots, until the script publishes), <c>false</c> while they are the device's per-phase sums, and <c>null</c> while the
    /// source is unknown, in which case both are <c>null</c>.
    /// </summary>
    [State(IsDiscrete = true, Position = 318)]
    public partial bool? IsTotalEnergyPhaseNetted { get; internal set; }

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
        TotalImportedEnergy = null;
        TotalExportedEnergy = null;
        ApparentPower = null;
        ElectricalCurrent = null;
        NeutralCurrent = null;
        TotalImportedPhaseEnergy = null;
        TotalExportedPhaseEnergy = null;
        IsTotalEnergyPhaseNetted = null;
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
        MeasuredPower = status.TotalActivePower;
        ApparentPower = status.TotalApparentPower;
        ElectricalCurrent = status.TotalCurrent;
        NeutralCurrent = status.NeutralCurrent;

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
        TotalImportedPhaseEnergy = dataStatus.TotalActiveEnergy;
        TotalExportedPhaseEnergy = dataStatus.TotalActiveReturnedEnergy;

        Phases[0].TotalImportedEnergy = dataStatus.PhaseATotalActiveEnergy;
        Phases[0].TotalExportedEnergy = dataStatus.PhaseATotalReturnedEnergy;
        Phases[1].TotalImportedEnergy = dataStatus.PhaseBTotalActiveEnergy;
        Phases[1].TotalExportedEnergy = dataStatus.PhaseBTotalReturnedEnergy;
        Phases[2].TotalImportedEnergy = dataStatus.PhaseCTotalActiveEnergy;
        Phases[2].TotalExportedEnergy = dataStatus.PhaseCTotalReturnedEnergy;

        if (IsTotalEnergyPhaseNetted == false)
            UsePhaseEnergy();
    }

    /// <summary>
    /// Switches <see cref="TotalImportedEnergy"/> and <see cref="TotalExportedEnergy"/> to the per-phase sums.
    /// </summary>
    internal void UsePhaseEnergy()
    {
        IsTotalEnergyPhaseNetted = false;
        TotalImportedEnergy = TotalImportedPhaseEnergy;
        TotalExportedEnergy = TotalExportedPhaseEnergy;
    }

    /// <summary>
    /// Gets a value indicating whether this meter showed a script value.
    /// </summary>
    internal bool HasUsedScriptValues { get; private set; }

    /// <summary>
    /// Sets <see cref="TotalImportedEnergy"/> and <see cref="TotalExportedEnergy"/> to the given script values.
    /// </summary>
    internal void UseScriptValues(decimal? importedValue, decimal? exportedValue)
    {
        IsTotalEnergyPhaseNetted = true;
        TotalImportedEnergy = importedValue;
        TotalExportedEnergy = exportedValue;

        if (importedValue != null || exportedValue != null)
            HasUsedScriptValues = true;
    }

    /// <summary>
    /// Marks the source as unknown and clears <see cref="TotalImportedEnergy"/> and <see cref="TotalExportedEnergy"/>.
    /// </summary>
    internal void ClearTotalEnergy()
    {
        IsTotalEnergyPhaseNetted = null;
        TotalImportedEnergy = null;
        TotalExportedEnergy = null;
    }
}
