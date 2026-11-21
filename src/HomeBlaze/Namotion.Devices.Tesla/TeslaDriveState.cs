using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The location and driving state of a Tesla vehicle.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaDriveState : ITitleProvider, IIconProvider
{
    // The cached snapshot has separate timestamps for the location and the drive section.
    private readonly ILogger _logger;
    private readonly TeslaSectionClock _locationClock = new();
    private readonly TeslaSectionClock _driveClock = new();

    // The JSON reports Homelink in the vehicle state, the snapshot in the location state, each with its own timestamp.
    private readonly TeslaSectionClock _homelinkClock = new();

    public string Title => "Drive";

    public string IconName => "Speed";

    [State(Position = 1)] public partial GeoCoordinate? Location { get; internal set; }

    /// <summary>The location in the coordinate system of <see cref="NativeLocationType"/>, which differs from <see cref="Location"/> only in some regions.</summary>
    [State(Position = 2)] public partial GeoCoordinate? NativeLocation { get; internal set; }

    [State(Position = 3)] public partial TeslaGpsCoordinateType? NativeLocationType { get; internal set; }
    [State(IsDiscrete = true, Position = 4)] public partial bool? IsNativeLocationSupported { get; internal set; }
    [State(Unit = StateUnit.Degree, Position = 5)] public partial decimal? Heading { get; internal set; }

    /// <summary>The time of the last GPS fix.</summary>
    [State(Position = 6)] public partial DateTimeOffset? GpsTime { get; internal set; }

    /// <summary>Whether the vehicle is near a configured Homelink device.</summary>
    [State(IsDiscrete = true, Position = 7)] public partial bool? IsHomelinkNearby { get; internal set; }

    /// <summary>The speed, which Tesla omits while the vehicle is parked.</summary>
    [State(Unit = StateUnit.KilometerPerHour, Position = 8)] public partial decimal? Speed { get; internal set; }

    /// <summary>The drive power, which is negative while the vehicle regenerates.</summary>
    [State(Unit = StateUnit.Watt, Position = 9)] public partial decimal? Power { get; internal set; }

    [State(IsDiscrete = true, Position = 10)] public partial TeslaShiftState? ShiftState { get; internal set; }

    /// <summary>Whether the vehicle is in drive, reverse or neutral, from the shift state.</summary>
    [Derived]
    [State(IsDiscrete = true, Position = 11)]
    public bool? IsDriving => ShiftState switch
    {
        null or TeslaShiftState.Unknown => null,
        TeslaShiftState.Park => false,
        _ => true
    };

    [State(Position = 12)] public partial TeslaNavigation Navigation { get; internal set; }

    internal TeslaDriveState(ILogger logger)
    {
        _logger = logger;
        Location = null;
        NativeLocation = null;
        NativeLocationType = null;
        IsNativeLocationSupported = null;
        Heading = null;
        GpsTime = null;
        IsHomelinkNearby = null;
        Speed = null;
        Power = null;
        ShiftState = null;
        Navigation = new TeslaNavigation();
    }

    internal void Update(TeslaDriveStateDto dto)
    {
        // The JSON has one timestamp for both sections. Both clocks advance, so no check may short-circuit the other.
        var timestamp = TeslaValues.FromUnixMilliseconds(dto.Timestamp);
        var isLocationNewer = _locationClock.TryAdvance(timestamp);
        var isDriveNewer = _driveClock.TryAdvance(timestamp);

        if (isLocationNewer)
        {
            UpdateLocationFromJson(dto);
        }

        if (isDriveNewer)
        {
            UpdateDriveFromJson(dto);
        }
    }

    internal void Update(LocationState proto)
    {
        if (!_locationClock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateCoordinatesFromProtobuf(proto);
        UpdateLocationDetailsFromProtobuf(proto);
    }

    internal void Update(DriveState proto)
    {
        if (!_driveClock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateDriveFromProtobuf(proto);
        Navigation.UpdateFromProtobuf(proto);
    }

    /// <param name="isNearby">Whether the vehicle is near a Homelink device.</param>
    /// <param name="timestamp">The timestamp of the section that reports it.</param>
    internal void UpdateHomelinkNearby(bool? isNearby, DateTimeOffset? timestamp)
    {
        if (_homelinkClock.TryAdvance(timestamp))
        {
            IsHomelinkNearby = isNearby;
        }
    }

    private void UpdateLocationFromJson(TeslaDriveStateDto dto)
    {
        Location = TeslaValues.Coordinate(TeslaValues.ToFloatPrecision(dto.Latitude), TeslaValues.ToFloatPrecision(dto.Longitude));
        NativeLocation = TeslaValues.Coordinate(TeslaValues.ToFloatPrecision(dto.NativeLatitude), TeslaValues.ToFloatPrecision(dto.NativeLongitude));
        NativeLocationType = TeslaValues.Enum<TeslaGpsCoordinateType>(dto.NativeType, _logger);
        IsNativeLocationSupported = dto.NativeLocationSupported is { } supported ? supported > 0 : null;
        Heading = dto.Heading;
        GpsTime = TeslaValues.FromUnixSeconds(dto.GpsAsOf);
    }

    private void UpdateDriveFromJson(TeslaDriveStateDto dto)
    {
        // Tesla omits the shift state and the speed while the vehicle is parked.
        ShiftState = TeslaValues.ShiftState(dto.ShiftState) ?? (dto.Speed is null ? TeslaShiftState.Park : null);
        Speed = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.WholeNumber(dto.Speed));
        Power = TeslaValues.KilowattsToWatts(dto.Power);
        Navigation.UpdateFromJson(dto);
    }

    private void UpdateCoordinatesFromProtobuf(LocationState proto)
    {
        if (proto.OptionalLatitudeCase != LocationState.OptionalLatitudeOneofCase.None && proto.OptionalLongitudeCase != LocationState.OptionalLongitudeOneofCase.None)
        {
            Location = TeslaValues.Coordinate(TeslaValues.Decimal(proto.Latitude), TeslaValues.Decimal(proto.Longitude));
        }

        if (proto.OptionalNativeLatitudeCase != LocationState.OptionalNativeLatitudeOneofCase.None && proto.OptionalNativeLongitudeCase != LocationState.OptionalNativeLongitudeOneofCase.None)
        {
            NativeLocation = TeslaValues.Coordinate(TeslaValues.Decimal(proto.NativeLatitude), TeslaValues.Decimal(proto.NativeLongitude));
        }
    }

    private void UpdateLocationDetailsFromProtobuf(LocationState proto)
    {
        if (proto.NativeType is { } nativeType && TeslaProtoValues.CoordinateType(nativeType) is { } mappedType) NativeLocationType = mappedType;
        if (proto.OptionalNativeLocationSupportedCase != LocationState.OptionalNativeLocationSupportedOneofCase.None) IsNativeLocationSupported = proto.NativeLocationSupported;
        if (proto.OptionalHeadingCase != LocationState.OptionalHeadingOneofCase.None) Heading = proto.Heading;
        if (proto.OptionalGpsAsOfCase != LocationState.OptionalGpsAsOfOneofCase.None) GpsTime = TeslaValues.FromUnixSeconds((long)proto.GpsAsOf);
        if (proto.OptionalHomelinkNearbyCase != LocationState.OptionalHomelinkNearbyOneofCase.None) UpdateHomelinkNearby(proto.HomelinkNearby, proto.Timestamp?.ToDateTimeOffset());
    }

    private void UpdateDriveFromProtobuf(DriveState proto)
    {
        if (proto.OptionalPowerCase != DriveState.OptionalPowerOneofCase.None) Power = TeslaValues.KilowattsToWatts(proto.Power);
        UpdateShiftStateFromProtobuf(proto);
        UpdateSpeedFromProtobuf(proto);
    }

    // A parked vehicle reports an invalid shift state and no speed, where the JSON omits both.
    private void UpdateShiftStateFromProtobuf(DriveState proto)
    {
        if (proto.ShiftState is not { } shiftState)
        {
            return;
        }

        var mapped = TeslaProtoValues.ShiftState(shiftState);
        if (mapped is null && !HasSpeed(proto))
        {
            mapped = TeslaShiftState.Park;
        }

        if (mapped is not null)
        {
            ShiftState = mapped;
        }
    }

    // The JSON speed is whole miles per hour, so the fractional speed of the snapshot is rounded to match.
    private void UpdateSpeedFromProtobuf(DriveState proto)
    {
        if (proto.OptionalSpeedCase != DriveState.OptionalSpeedOneofCase.None) Speed = TeslaValues.MilesPerHourToKilometersPerHour(proto.Speed);
        else if (proto.OptionalSpeedFloatCase != DriveState.OptionalSpeedFloatOneofCase.None) Speed = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.WholeNumber(TeslaValues.Decimal(proto.SpeedFloat)));
    }

    private static bool HasSpeed(DriveState proto) =>
        proto.OptionalSpeedCase != DriveState.OptionalSpeedOneofCase.None || proto.OptionalSpeedFloatCase != DriveState.OptionalSpeedFloatOneofCase.None;
}
