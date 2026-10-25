using HomeBlaze.Abstractions.Devices.Energy;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.SunSpec.Models;

public partial class SunSpecStorageCapacity : IBatteryState
{
    /// <inheritdoc />
    [Derived]
    public decimal? BatteryLevel => SoC;
}
