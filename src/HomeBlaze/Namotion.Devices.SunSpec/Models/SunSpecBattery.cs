using HomeBlaze.Abstractions.Devices.Energy;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.SunSpec.Models;

public partial class SunSpecBattery : IBatteryState
{
    /// <inheritdoc />
    [Derived]
    public decimal? BatteryLevel => SoC;
}
