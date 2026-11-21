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

    /// <summary>Whether the vehicle is near a configured Homelink device. The vehicle subject sets it from the vehicle state of the JSON.</summary>
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

    public TeslaDriveState(ILogger logger)
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

    private void UpdateLocationFromJson(TeslaDriveStateDto dto)
    {
        Location = TeslaValues.Coordinate(dto.Latitude, dto.Longitude);
        NativeLocation = TeslaValues.Coordinate(dto.NativeLatitude, dto.NativeLongitude);
        NativeLocationType = TeslaValues.Enum<TeslaGpsCoordinateType>(dto.NativeType, _logger);
        IsNativeLocationSupported = dto.NativeLocationSupported is { } supported ? supported > 0 : null;
        Heading = dto.Heading;
        GpsTime = TeslaValues.FromUnixSeconds(dto.GpsAsOf);
    }

    private void UpdateDriveFromJson(TeslaDriveStateDto dto)
    {
        // Tesla omits the shift state and the speed while the vehicle is parked.
        ShiftState = TeslaValues.ShiftState(dto.ShiftState) ?? (dto.Speed is null ? TeslaShiftState.Park : null);
        Speed = TeslaValues.MilesPerHourToKilometersPerHour(dto.Speed);
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
        if (proto.NativeType is { } nativeType) NativeLocationType = TeslaProtoValues.CoordinateType(nativeType);
        if (proto.OptionalNativeLocationSupportedCase != LocationState.OptionalNativeLocationSupportedOneofCase.None) IsNativeLocationSupported = proto.NativeLocationSupported;
        if (proto.OptionalHeadingCase != LocationState.OptionalHeadingOneofCase.None) Heading = proto.Heading;
        if (proto.OptionalGpsAsOfCase != LocationState.OptionalGpsAsOfOneofCase.None) GpsTime = TeslaValues.FromUnixSeconds((long)proto.GpsAsOf);
        if (proto.OptionalHomelinkNearbyCase != LocationState.OptionalHomelinkNearbyOneofCase.None) IsHomelinkNearby = proto.HomelinkNearby;
    }

    private void UpdateDriveFromProtobuf(DriveState proto)
    {
        if (proto.ShiftState is { } shiftState) ShiftState = TeslaProtoValues.ShiftState(shiftState);
        if (proto.OptionalPowerCase != DriveState.OptionalPowerOneofCase.None) Power = TeslaValues.KilowattsToWatts(proto.Power);

        if (proto.OptionalSpeedFloatCase != DriveState.OptionalSpeedFloatOneofCase.None) Speed = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.Decimal(proto.SpeedFloat));
        else if (proto.OptionalSpeedCase != DriveState.OptionalSpeedOneofCase.None) Speed = TeslaValues.MilesPerHourToKilometersPerHour(proto.Speed);
    }
}
