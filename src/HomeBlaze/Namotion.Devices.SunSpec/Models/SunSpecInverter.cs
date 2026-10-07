using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.SunSpec.Models;

// The AC output is the measured circuit: production flows out of it (export), so MeasuredPower is -W.
public partial class SunSpecInverter : IPowerMeter, IElectricalFrequencySensor, ITemperatureSensor
{
    /// <inheritdoc />
    [Derived]
    public decimal? MeasuredPower => -W;

    /// <inheritdoc />
    [Derived]
    public decimal? TotalImportedEnergy => null;

    /// <inheritdoc />
    [Derived]
    public decimal? TotalExportedEnergy => WH;

    /// <inheritdoc />
    [Derived]
    public decimal? ElectricalFrequency => Hz;

    /// <inheritdoc />
    [Derived]
    public decimal? Temperature => TmpCab;
}
