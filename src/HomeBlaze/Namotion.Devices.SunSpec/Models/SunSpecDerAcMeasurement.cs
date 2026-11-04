using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.SunSpec.Models;

// The DER's AC terminals are the measured circuit: W is positive for generation (export), so MeasuredPower is -W.
public partial class SunSpecDerAcMeasurement : IPowerMeter, IElectricalFrequencySensor, ITemperatureSensor
{
    /// <inheritdoc />
    [Derived]
    public decimal? MeasuredPower => -W;

    /// <inheritdoc />
    [Derived]
    public decimal? TotalImportedEnergy => TotWhAbs;

    /// <inheritdoc />
    [Derived]
    public decimal? TotalExportedEnergy => TotWhInj;

    /// <inheritdoc />
    [Derived]
    public decimal? ElectricalFrequency => Hz;

    /// <inheritdoc />
    [Derived]
    public decimal? Temperature => TmpCab;
}
