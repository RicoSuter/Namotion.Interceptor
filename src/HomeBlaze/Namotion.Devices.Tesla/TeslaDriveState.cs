using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The driving state of a Tesla vehicle: heading, GPS time, speed, power, shift state and navigation. It sets the location of its vehicle from the location section.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaDriveState : ITitleProvider, IIconProvider
{
    // The owner, never an intercepted property: an intercepted property holding it would attach the vehicle under its own child.
    private readonly TeslaVehicle _vehicle;

    // The cached snapshot has separate timestamps for the location and the drive section.
    private readonly TeslaSectionClock _locationClock = new();
    private readonly TeslaSectionClock _driveClock = new();

    // The JSON reports Homelink in the vehicle state, the snapshot in the location state, each with its own timestamp.
    private readonly TeslaSectionClock _homelinkClock = new();

    public string Title => "Drive";

    public string IconName => "Speed";

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

    internal TeslaDriveState(TeslaVehicle vehicle)
    {
        _vehicle = vehicle;
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
        _vehicle.Location = TeslaValues.Coordinate(TeslaValues.ToFloatPrecision(dto.Latitude), TeslaValues.ToFloatPrecision(dto.Longitude));
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
            _vehicle.Location = TeslaValues.Coordinate(TeslaValues.Decimal(proto.Latitude), TeslaValues.Decimal(proto.Longitude));
        }
    }

    private void UpdateLocationDetailsFromProtobuf(LocationState proto)
    {
        if (proto.OptionalHeadingCase != LocationState.OptionalHeadingOneofCase.None) Heading = proto.Heading;
        if (proto.OptionalGpsAsOfCase != LocationState.OptionalGpsAsOfOneofCase.None) GpsTime = TeslaValues.FromUnixSeconds((long)proto.GpsAsOf);
        if (proto.OptionalHomelinkNearbyCase != LocationState.OptionalHomelinkNearbyOneofCase.None) UpdateHomelinkNearby(proto.HomelinkNearby, proto.Timestamp?.ToDateTimeOffset());
    }

    private void UpdateDriveFromProtobuf(DriveState proto)
    {
        if (proto.OptionalPowerCase != DriveState.OptionalPowerOneofCase.None) Power = TeslaValues.KilowattsToWatts(proto.Power);
        var shiftState = UpdateShiftStateFromProtobuf(proto);
        UpdateSpeedFromProtobuf(proto, shiftState);
    }

    /// <returns>The shift state this snapshot reports, or null.</returns>
    private TeslaShiftState? UpdateShiftStateFromProtobuf(DriveState proto)
    {
        if (proto.ShiftState is not { } shiftState)
        {
            return null;
        }

        // A parked vehicle reports an invalid shift state and no speed, where the JSON omits both.
        var mapped = TeslaProtoValues.ShiftState(shiftState);
        if (mapped is null && !HasSpeed(proto))
        {
            mapped = TeslaShiftState.Park;
        }

        if (mapped is not null)
        {
            ShiftState = mapped;
        }

        return mapped;
    }

    // The JSON speed is whole miles per hour, so the fractional speed of the snapshot is rounded to match. A parked
    // vehicle has no speed, like the JSON that omits it.
    private void UpdateSpeedFromProtobuf(DriveState proto, TeslaShiftState? shiftState)
    {
        if (proto.OptionalSpeedCase != DriveState.OptionalSpeedOneofCase.None) Speed = TeslaValues.MilesPerHourToKilometersPerHour(proto.Speed);
        else if (proto.OptionalSpeedFloatCase != DriveState.OptionalSpeedFloatOneofCase.None) Speed = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.WholeNumber(TeslaValues.Decimal(proto.SpeedFloat)));
        else if (shiftState == TeslaShiftState.Park) Speed = null;
    }

    private static bool HasSpeed(DriveState proto) =>
        proto.OptionalSpeedCase != DriveState.OptionalSpeedOneofCase.None || proto.OptionalSpeedFloatCase != DriveState.OptionalSpeedFloatOneofCase.None;
}
