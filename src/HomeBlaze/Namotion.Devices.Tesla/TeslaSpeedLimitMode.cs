using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The speed limit mode of a Tesla vehicle.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaSpeedLimitMode : ITitleProvider, IIconProvider
{
    private readonly TeslaSectionClock _clock = new();

    public string Title => "Speed Limit";

    public string IconName => "Speed";

    [State(IsDiscrete = true, Position = 1)] public partial bool? IsActive { get; internal set; }

    /// <summary>The limit that applies while the mode is active.</summary>
    [State(Unit = StateUnit.KilometerPerHour, Position = 2)] public partial decimal? CurrentLimit { get; internal set; }

    [State(Unit = StateUnit.KilometerPerHour, Position = 3)] public partial decimal? MinimumLimit { get; internal set; }
    [State(Unit = StateUnit.KilometerPerHour, Position = 4)] public partial decimal? MaximumLimit { get; internal set; }
    [State(IsDiscrete = true, Position = 5)] public partial bool? IsPinCodeSet { get; internal set; }

    internal TeslaSpeedLimitMode()
    {
        IsActive = null;
        CurrentLimit = null;
        MinimumLimit = null;
        MaximumLimit = null;
        IsPinCodeSet = null;
    }

    /// <param name="dto">The speed limit mode of the JSON vehicle state.</param>
    /// <param name="timestamp">The timestamp of that vehicle state, because the speed limit mode has none.</param>
    internal void Update(TeslaSpeedLimitModeDto dto, DateTimeOffset? timestamp)
    {
        if (!_clock.TryAdvance(timestamp))
        {
            return;
        }

        IsActive = dto.Active;
        CurrentLimit = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.ToFloatPrecision(dto.CurrentLimitMph));
        MinimumLimit = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.ToFloatPrecision(dto.MinLimitMph));
        MaximumLimit = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.ToFloatPrecision(dto.MaxLimitMph));
        IsPinCodeSet = dto.PinCodeSet;
    }

    /// <param name="proto">The speed limit mode of the closures state of the snapshot.</param>
    /// <param name="timestamp">The timestamp of that closures state, because the speed limit mode has none.</param>
    internal void Update(SpeedLimitMode proto, DateTimeOffset? timestamp)
    {
        if (!_clock.TryAdvance(timestamp))
        {
            return;
        }

        UpdateLimitsFromProtobuf(proto);
        if (proto.OptionalActiveCase != SpeedLimitMode.OptionalActiveOneofCase.None) IsActive = proto.Active;
        if (proto.OptionalPinCodeSetCase != SpeedLimitMode.OptionalPinCodeSetOneofCase.None) IsPinCodeSet = proto.PinCodeSet;
    }

    private void UpdateLimitsFromProtobuf(SpeedLimitMode proto)
    {
        if (proto.OptionalCurrentLimitMphCase != SpeedLimitMode.OptionalCurrentLimitMphOneofCase.None) CurrentLimit = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.Decimal(proto.CurrentLimitMph));
        if (proto.OptionalMinLimitMphCase != SpeedLimitMode.OptionalMinLimitMphOneofCase.None) MinimumLimit = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.Decimal(proto.MinLimitMph));
        if (proto.OptionalMaxLimitMphCase != SpeedLimitMode.OptionalMaxLimitMphOneofCase.None) MaximumLimit = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.Decimal(proto.MaxLimitMph));
    }
}
