using HomeBlaze.Abstractions.Devices.Energy;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.SunSpec.Models;

public partial class SunSpecStorageCapacity : IBatteryState
{
    /// <inheritdoc />
    [Derived]
    public decimal? BatteryLevel => SoC;

    /// <summary>
    /// Always null: a storage battery is discharged to its reserve by design, which does not mean that it needs charging or replacing.
    /// </summary>
    [Derived]
    public bool? IsBatteryLow => null;
}
