using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.SunSpec.Models;

// The direction of W is passed through unchanged; whether a device reports import as positive is not verified yet.
public partial class SunSpecFloatAcMeter : IPowerMeter, IElectricalFrequencySensor
{
    /// <inheritdoc />
    [Derived]
    public decimal? MeasuredPower => W;

    /// <inheritdoc />
    [Derived]
    public decimal? TotalImportedEnergy => TotWhImp;

    /// <inheritdoc />
    [Derived]
    public decimal? TotalExportedEnergy => TotWhExp;

    /// <inheritdoc />
    [Derived]
    public decimal? ElectricalFrequency => Hz;
}
