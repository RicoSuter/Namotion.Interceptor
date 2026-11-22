using CarServer;
using HomeBlaze.Abstractions.Common;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaClimateAndDriveTests
{
    private const long FixtureTimestamp = 1795293677353;

    private static TeslaVehicleDataDto LoadFixture() =>
        JsonSerializer.Deserialize(FixtureLoader.Load("vehicle-data.json"), TeslaJsonContext.Default.TeslaResponseTeslaVehicleDataDto)!.Response!;

    private static VehicleData CreateSnapshot(long offsetMilliseconds) =>
        VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp + offsetMilliseconds));

    private static TeslaClimateState CreateClimateFromFixture()
    {
        var climate = new TeslaClimateState(NullLogger.Instance);
        climate.Update(LoadFixture().ClimateState!);
        return climate;
    }

    // The drive state sets the location of its vehicle, so it is created through one.
    private static TeslaVehicle CreateVehicle() => new(1, "TESTVIN0000000001", TimeProvider.System, NullLogger.Instance);

    private static TeslaVehicle CreateVehicleFromDriveFixture()
    {
        var vehicle = CreateVehicle();
        vehicle.Drive.Update(LoadFixture().DriveState!);
        return vehicle;
    }

    [Fact]
    public void WhenClimateFixtureIsApplied_ThenTemperaturesAndModesAreMapped()
    {
        // Act
        var climate = CreateClimateFromFixture();

        // Assert
        Assert.False(climate.IsClimateOn);
        Assert.Equal(12.1m, climate.InsideTemperature);
        Assert.Equal(11.5m, climate.OutsideTemperature);
        Assert.Equal(21.0m, climate.DriverTemperatureSetting);
        Assert.Equal(TeslaCabinOverheatProtection.Off, climate.CabinOverheatProtection);
        Assert.Equal(TeslaCabinOverheatProtectionTemperature.High, climate.CabinOverheatProtectionTemperature);
        Assert.Equal(TeslaClimateKeeperMode.Off, climate.ClimateKeeperMode);
        Assert.Equal(0, climate.SeatHeaters.FrontLeftLevel);
        Assert.True(climate.SeatHeaters.IsAutoSeatClimateFrontLeftOn);
    }

    [Fact]
    public void WhenClimateFixtureIsApplied_ThenRemainingFieldsAreMapped()
    {
        // Act
        var climate = CreateClimateFromFixture();

        // Assert
        Assert.False(climate.IsAutoConditioningOn);
        Assert.False(climate.IsPreconditioning);
        Assert.Equal(21.0m, climate.PassengerTemperatureSetting);
        Assert.Equal(15.0m, climate.MinimumTemperatureSetting);
        Assert.Equal(28.0m, climate.MaximumTemperatureSetting);
        Assert.Equal(0, climate.FanStatus);
        Assert.Equal(0, climate.DefrostMode);
        Assert.Equal(0, climate.LeftTemperatureDirection);
        Assert.Equal(0, climate.RightTemperatureDirection);
        Assert.Equal(TeslaHvacAutoRequest.On, climate.HvacAutoRequest);
        Assert.False(climate.IsFrontDefrosterOn);
        Assert.False(climate.IsRearDefrosterOn);
        Assert.False(climate.IsSideMirrorHeaterOn);
        Assert.False(climate.IsWiperBladeHeaterOn);
        Assert.False(climate.IsBatteryHeaterOn);
        Assert.Null(climate.IsBatteryHeaterWithoutPower);
        Assert.False(climate.IsRemoteHeaterControlEnabled);
        Assert.False(climate.IsCabinOverheatProtectionAllowed);
        Assert.True(climate.IsFanOnlyCabinOverheatProtectionSupported);
        Assert.False(climate.IsCabinOverheatProtectionCooling);
        Assert.Equal(0, climate.SeatHeaters.FrontRightLevel);
        Assert.Equal(0, climate.SeatHeaters.RearLeftLevel);
        Assert.Equal(0, climate.SeatHeaters.RearCenterLevel);
        Assert.Equal(0, climate.SeatHeaters.RearRightLevel);
        Assert.True(climate.SeatHeaters.IsAutoSeatClimateFrontRightOn);
        Assert.False(climate.SeatHeaters.IsSteeringWheelHeaterOn);
        Assert.Equal(0, climate.SeatHeaters.SteeringWheelHeatLevel);
        Assert.True(climate.SeatHeaters.IsAutoSteeringWheelHeatOn);
    }

    [Fact]
    public void WhenClimateSnapshotIsNewer_ThenItUpdatesAndAbsentFieldsStay()
    {
        // Arrange
        var climate = CreateClimateFromFixture();
        var snapshot = CreateSnapshot(60_000);

        // Act
        climate.Update(snapshot.ClimateState);

        // Assert
        Assert.True(climate.IsClimateOn);
        Assert.Equal(20.5m, climate.InsideTemperature);
        Assert.Equal(10m, climate.OutsideTemperature);
        Assert.Equal(21.0m, climate.DriverTemperatureSetting); // not in the snapshot, unchanged
        Assert.Equal(TeslaCabinOverheatProtection.Off, climate.CabinOverheatProtection); // not in the snapshot, unchanged
        Assert.Equal(TeslaClimateKeeperMode.Off, climate.ClimateKeeperMode); // not in the snapshot, unchanged
        Assert.Equal(0, climate.SeatHeaters.FrontLeftLevel); // not in the snapshot, unchanged
    }

    [Fact]
    public void WhenClimateSnapshotHasAllFields_ThenTheyMatchTheJsonValues()
    {
        // Arrange
        var climate = CreateClimateFromFixture();
        var snapshot = CreateSnapshot(60_000);
        var proto = snapshot.ClimateState;
        proto.IsAutoConditioningOn = true;
        proto.IsPreconditioning = true;
        proto.DriverTempSetting = 22.5f;
        proto.PassengerTempSetting = 19f;
        proto.MinAvailTempCelsius = 16f;
        proto.MaxAvailTempCelsius = 27f;
        proto.FanStatus = 4;
        proto.LeftTempDirection = 1;
        proto.RightTempDirection = -1;
        proto.IsFrontDefrosterOn = true;
        proto.IsRearDefrosterOn = true;
        proto.SideMirrorHeaters = true;
        proto.WiperBladeHeater = true;
        proto.BatteryHeater = true;
        proto.BatteryHeaterNoPower = true;
        proto.RemoteHeaterControlEnabled = true;
        proto.AllowCabinOverheatProtection = true;
        proto.SupportsFanOnlyCabinOverheatProtection = false;
        proto.CabinOverheatProtection = ClimateState.Types.CabinOverheatProtection_E.CabinOverheatProtectionFanOnly;
        proto.CabinOverheatProtectionActivelyCooling = true;
        proto.CopActivationTemperature = ClimateState.Types.CopActivationTemp.Medium;
        proto.ClimateKeeperMode = new ClimateState.Types.ClimateKeeperMode { Party = new CarServer.Void() };
        proto.DefrostMode = new ClimateState.Types.DefrostMode { Max = new CarServer.Void() };
        proto.HvacAutoRequest = ClimateState.Types.HvacAutoRequest.Override;
        proto.SeatHeaterLeft = 3;
        proto.SeatHeaterRight = 2;
        proto.SeatHeaterRearLeft = 1;
        proto.SeatHeaterRearCenter = 2;
        proto.SeatHeaterRearRight = 3;
        proto.AutoSeatClimateLeft = false;
        proto.AutoSeatClimateRight = false;
        proto.SteeringWheelHeater = true;
        proto.SteeringWheelHeatLevel = StwHeatLevel.High;
        proto.AutoSteeringWheelHeat = false;

        // Act
        climate.Update(proto);

        // Assert
        Assert.True(climate.IsAutoConditioningOn);
        Assert.True(climate.IsPreconditioning);
        Assert.Equal(22.5m, climate.DriverTemperatureSetting);
        Assert.Equal(19m, climate.PassengerTemperatureSetting);
        Assert.Equal(16m, climate.MinimumTemperatureSetting);
        Assert.Equal(27m, climate.MaximumTemperatureSetting);
        Assert.Equal(4, climate.FanStatus);
        Assert.Equal(1, climate.LeftTemperatureDirection);
        Assert.Equal(-1, climate.RightTemperatureDirection);
        Assert.True(climate.IsFrontDefrosterOn);
        Assert.True(climate.IsRearDefrosterOn);
        Assert.True(climate.IsSideMirrorHeaterOn);
        Assert.True(climate.IsWiperBladeHeaterOn);
        Assert.True(climate.IsBatteryHeaterOn);
        Assert.True(climate.IsBatteryHeaterWithoutPower);
        Assert.True(climate.IsRemoteHeaterControlEnabled);
        Assert.True(climate.IsCabinOverheatProtectionAllowed);
        Assert.False(climate.IsFanOnlyCabinOverheatProtectionSupported);
        Assert.Equal(TeslaCabinOverheatProtection.FanOnly, climate.CabinOverheatProtection);
        Assert.True(climate.IsCabinOverheatProtectionCooling);
        Assert.Equal(TeslaCabinOverheatProtectionTemperature.Medium, climate.CabinOverheatProtectionTemperature);
        Assert.Equal(TeslaClimateKeeperMode.Camp, climate.ClimateKeeperMode);
        Assert.Equal(2, climate.DefrostMode);
        Assert.Equal(TeslaHvacAutoRequest.Override, climate.HvacAutoRequest);
        Assert.Equal(3, climate.SeatHeaters.FrontLeftLevel);
        Assert.Equal(2, climate.SeatHeaters.FrontRightLevel);
        Assert.Equal(1, climate.SeatHeaters.RearLeftLevel);
        Assert.Equal(2, climate.SeatHeaters.RearCenterLevel);
        Assert.Equal(3, climate.SeatHeaters.RearRightLevel);
        Assert.False(climate.SeatHeaters.IsAutoSeatClimateFrontLeftOn);
        Assert.False(climate.SeatHeaters.IsAutoSeatClimateFrontRightOn);
        Assert.True(climate.SeatHeaters.IsSteeringWheelHeaterOn);
        Assert.Equal(2, climate.SeatHeaters.SteeringWheelHeatLevel);
        Assert.False(climate.SeatHeaters.IsAutoSteeringWheelHeatOn);
    }

    [Fact]
    public void WhenClimateSnapshotReportsUnknownValues_ThenEnumValuesStay()
    {
        // Arrange
        var climate = CreateClimateFromFixture();
        var snapshot = CreateSnapshot(60_000);
        snapshot.ClimateState.CopActivationTemperature = ClimateState.Types.CopActivationTemp.Unspecified;
        snapshot.ClimateState.ClimateKeeperMode = new ClimateState.Types.ClimateKeeperMode { Unknown = new CarServer.Void() };
        snapshot.ClimateState.SteeringWheelHeatLevel = StwHeatLevel.Unknown;
        snapshot.ClimateState.InsideTempCelsius = float.NaN;

        // Act
        climate.Update(snapshot.ClimateState);

        // Assert
        Assert.Equal(TeslaCabinOverheatProtectionTemperature.High, climate.CabinOverheatProtectionTemperature);
        Assert.Equal(TeslaClimateKeeperMode.Off, climate.ClimateKeeperMode);
        Assert.Equal(0, climate.SeatHeaters.SteeringWheelHeatLevel);
        Assert.Null(climate.InsideTemperature);
    }

    [Fact]
    public void WhenClimateSnapshotIsOlder_ThenItIsIgnored()
    {
        // Arrange
        var climate = CreateClimateFromFixture();
        var snapshot = CreateSnapshot(-60_000);

        // Act
        climate.Update(snapshot.ClimateState);

        // Assert
        Assert.False(climate.IsClimateOn);
        Assert.Equal(12.1m, climate.InsideTemperature);
    }

    [Fact]
    public void WhenClimateSnapshotHasEqualTimestamp_ThenItApplies()
    {
        // Arrange
        var climate = CreateClimateFromFixture();
        var snapshot = CreateSnapshot(0);

        // Act
        climate.Update(snapshot.ClimateState);

        // Assert
        Assert.True(climate.IsClimateOn);
    }

    [Fact]
    public void WhenClimateJsonIsOlderThanAppliedSnapshot_ThenItIsIgnored()
    {
        // Arrange
        var climate = new TeslaClimateState(NullLogger.Instance);
        climate.Update(CreateSnapshot(60_000).ClimateState);

        // Act
        climate.Update(LoadFixture().ClimateState!);

        // Assert
        Assert.True(climate.IsClimateOn);
        Assert.Equal(20.5m, climate.InsideTemperature);
    }

    [Fact]
    public void WhenDriveFixtureIsApplied_ThenParkedVehicleIsNotDriving()
    {
        // Act
        var vehicle = CreateVehicleFromDriveFixture();
        var drive = vehicle.Drive;

        // Assert
        Assert.Equal(new GeoCoordinate(47.0, 8.0), vehicle.Location);
        Assert.Equal(261m, drive.Heading);
        Assert.Equal(TeslaShiftState.Park, drive.ShiftState);
        Assert.False(drive.IsDriving);
        Assert.Null(drive.Speed);
        Assert.Null(drive.Navigation.Destination);
    }

    [Fact]
    public void WhenDriveFixtureIsApplied_ThenRemainingFieldsAreMapped()
    {
        // Act
        var drive = CreateVehicleFromDriveFixture().Drive;

        // Assert
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795293363), drive.GpsTime);
        Assert.Equal(0m, drive.Power);
        Assert.Null(drive.IsHomelinkNearby);
        Assert.Null(drive.Navigation.DestinationLocation);
        Assert.Null(drive.Navigation.TimeToArrival);
    }

    [Fact]
    public void WhenVehicleDrivesAccordingToJson_ThenValuesAreConverted()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        var dto = new TeslaDriveStateDto { ShiftState = "D", Speed = 50m, Power = -12, Timestamp = FixtureTimestamp };

        // Act
        drive.Update(dto);

        // Assert
        Assert.Equal(TeslaShiftState.Drive, drive.ShiftState);
        Assert.True(drive.IsDriving);
        Assert.Equal(80.4672m, drive.Speed);
        Assert.Equal(-12000m, drive.Power);
    }

    [Fact]
    public void WhenShiftStateAndSpeedAreMissingInJson_ThenVehicleIsParked()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        var dto = new TeslaDriveStateDto { Timestamp = FixtureTimestamp };

        // Act
        drive.Update(dto);

        // Assert
        Assert.Equal(TeslaShiftState.Park, drive.ShiftState);
        Assert.False(drive.IsDriving);
    }

    [Fact]
    public void WhenOnlyShiftStateIsMissingInJson_ThenShiftStateAndIsDrivingAreNull()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        var dto = new TeslaDriveStateDto { Speed = 5m, Timestamp = FixtureTimestamp };

        // Act
        drive.Update(dto);

        // Assert
        Assert.Null(drive.ShiftState);
        Assert.Null(drive.IsDriving);
    }

    [Fact]
    public void WhenRouteIsActiveInJson_ThenNavigationIsMapped()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        var dto = new TeslaDriveStateDto
        {
            ActiveRouteDestination = "Test Destination",
            ActiveRouteLatitude = 47.25m,
            ActiveRouteLongitude = 8.25m,
            ActiveRouteMinutesToArrival = 12.5m,
            ActiveRouteMilesToArrival = 10m,
            ActiveRouteEnergyAtArrival = 40,
            ActiveRouteTrafficMinutesDelay = 3m,
            Timestamp = FixtureTimestamp
        };

        // Act
        drive.Update(dto);

        // Assert
        Assert.Equal("Test Destination", drive.Navigation.Destination);
        Assert.Equal(new GeoCoordinate(47.25, 8.25), drive.Navigation.DestinationLocation);
        Assert.Equal(TimeSpan.FromMinutes(12.5), drive.Navigation.TimeToArrival);
        Assert.Equal(16.09344m, drive.Navigation.DistanceToArrival);
        Assert.Equal(0.4m, drive.Navigation.EnergyAtArrival);
        Assert.Equal(TimeSpan.FromMinutes(3), drive.Navigation.TrafficDelay);
    }

    [Fact]
    public void WhenRouteEndsInJson_ThenNavigationIsCleared()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        drive.Update(new TeslaDriveStateDto { ActiveRouteDestination = "Test Destination", ActiveRouteMinutesToArrival = 12m, Timestamp = FixtureTimestamp });

        // Act
        drive.Update(new TeslaDriveStateDto { ActiveRouteMinutesToArrival = 0m, Timestamp = FixtureTimestamp + 1000 });

        // Assert
        Assert.Null(drive.Navigation.Destination);
        Assert.Null(drive.Navigation.TimeToArrival);
    }

    [Fact]
    public void WhenSnapshotLocationIsNewer_ThenLocationUpdates()
    {
        // Arrange
        var vehicle = CreateVehicleFromDriveFixture();
        var drive = vehicle.Drive;
        var snapshot = CreateSnapshot(60_000);

        // Act
        drive.Update(snapshot.LocationState);

        // Assert
        Assert.Equal(47.5, vehicle.Location!.Value.Latitude, 3);
        Assert.Equal(90m, drive.Heading);
        Assert.Equal(TeslaShiftState.Park, drive.ShiftState); // drive section is not part of the location snapshot
    }

    [Fact]
    public void WhenSnapshotLocationHasAllFields_ThenTheyAreMapped()
    {
        // Arrange
        var vehicle = CreateVehicleFromDriveFixture();
        var drive = vehicle.Drive;
        var snapshot = CreateSnapshot(60_000);
        snapshot.LocationState.GpsAsOf = 1795293999;
        snapshot.LocationState.HomelinkNearby = true;

        // Act
        drive.Update(snapshot.LocationState);

        // Assert
        Assert.Equal(8.5, vehicle.Location!.Value.Longitude, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795293999), drive.GpsTime);
        Assert.True(drive.IsHomelinkNearby);
    }

    [Fact]
    public void WhenSnapshotLocationIsOlder_ThenItIsIgnored()
    {
        // Arrange
        var vehicle = CreateVehicleFromDriveFixture();
        var drive = vehicle.Drive;
        var snapshot = CreateSnapshot(-60_000);

        // Act
        drive.Update(snapshot.LocationState);

        // Assert
        Assert.Equal(new GeoCoordinate(47.0, 8.0), vehicle.Location);
        Assert.Equal(261m, drive.Heading);
    }

    [Fact]
    public void WhenSnapshotLocationHasEqualTimestamp_ThenItApplies()
    {
        // Arrange
        var drive = CreateVehicleFromDriveFixture().Drive;
        var snapshot = CreateSnapshot(0);

        // Act
        drive.Update(snapshot.LocationState);

        // Assert
        Assert.Equal(90m, drive.Heading);
    }

    [Fact]
    public void WhenSnapshotLocationHasNonFiniteCoordinate_ThenLocationBecomesNull()
    {
        // Arrange
        var vehicle = CreateVehicleFromDriveFixture();
        var drive = vehicle.Drive;
        var snapshot = CreateSnapshot(60_000);
        snapshot.LocationState.Latitude = float.NaN;

        // Act
        drive.Update(snapshot.LocationState);

        // Assert
        Assert.Null(vehicle.Location);
    }

    [Fact]
    public void WhenDriveSnapshotIsNewer_ThenItUpdatesAndAbsentFieldsStay()
    {
        // Arrange
        var vehicle = CreateVehicleFromDriveFixture();
        var drive = vehicle.Drive;
        var snapshot = CreateSnapshot(60_000);

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(TeslaShiftState.Drive, drive.ShiftState);
        Assert.True(drive.IsDriving);
        Assert.Equal(80.4672m, drive.Speed);
        Assert.Equal(20000m, drive.Power);
        Assert.Equal(new GeoCoordinate(47.0, 8.0), vehicle.Location); // location section is not part of the drive snapshot
    }

    [Fact]
    public void WhenDriveSnapshotHasOnlyIntegerSpeed_ThenItIsUsed()
    {
        // Arrange
        var drive = CreateVehicleFromDriveFixture().Drive;
        var snapshot = CreateSnapshot(60_000);
        snapshot.DriveState.ClearSpeedFloat();
        snapshot.DriveState.Speed = 30;

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(48.28032m, drive.Speed);
    }

    [Fact]
    public void WhenParkedSnapshotHasInvalidShiftStateAndNoSpeed_ThenVehicleIsParked()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        drive.Update(CreateSnapshot(0).DriveState);
        var snapshot = CreateSnapshot(60_000);
        snapshot.DriveState.ShiftState = new ShiftState { Invalid = new CarServer.Void() };
        snapshot.DriveState.ClearSpeedFloat();

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(TeslaShiftState.Park, drive.ShiftState);
        Assert.False(drive.IsDriving);
    }

    [Fact]
    public void WhenSnapshotHasSnaShiftStateAndNoSpeed_ThenVehicleIsParked()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        var snapshot = CreateSnapshot(0);
        snapshot.DriveState.ShiftState = new ShiftState { SNA = new CarServer.Void() };
        snapshot.DriveState.ClearSpeedFloat();

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(TeslaShiftState.Park, drive.ShiftState);
    }

    [Fact]
    public void WhenSnapshotHasInvalidShiftStateAndSpeed_ThenShiftStateStays()
    {
        // Arrange
        var drive = CreateVehicleFromDriveFixture().Drive;
        var snapshot = CreateSnapshot(60_000);
        snapshot.DriveState.ShiftState = new ShiftState { Invalid = new CarServer.Void() };

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(TeslaShiftState.Park, drive.ShiftState);
        Assert.False(drive.IsDriving);
    }

    [Fact]
    public void WhenSnapshotAndJsonDescribeTheSameCoordinate_ThenTheValuesAreEqual()
    {
        // Arrange
        var jsonVehicle = CreateVehicle();
        var jsonDrive = jsonVehicle.Drive;
        jsonDrive.Update(new TeslaDriveStateDto
        {
            Latitude = 47.123456789m,
            Longitude = 8.987654321m,
            ActiveRouteDestination = "Test Destination",
            ActiveRouteLatitude = 47.123456789m,
            ActiveRouteLongitude = 8.987654321m,
            Timestamp = FixtureTimestamp
        });
        var snapshot = CreateSnapshot(0);
        snapshot.LocationState.Latitude = 47.123456789f;
        snapshot.LocationState.Longitude = 8.987654321f;
        snapshot.DriveState.ActiveRouteDestination = "Test Destination";
        snapshot.DriveState.ActiveRouteCoordinates = new LatLong { Latitude = 47.123456789f, Longitude = 8.987654321f };
        var protobufVehicle = CreateVehicle();
        var protobufDrive = protobufVehicle.Drive;

        // Act
        protobufDrive.Update(snapshot.LocationState);
        protobufDrive.Update(snapshot.DriveState);

        // Assert
        Assert.NotNull(jsonVehicle.Location);
        Assert.Equal(jsonVehicle.Location, protobufVehicle.Location);
        Assert.Equal(jsonDrive.Navigation.DestinationLocation, protobufDrive.Navigation.DestinationLocation);
    }

    [Fact]
    public void WhenSnapshotHasFractionalSpeed_ThenItIsRoundedLikeTheJson()
    {
        // Arrange
        var drive = CreateVehicleFromDriveFixture().Drive;
        var snapshot = CreateSnapshot(60_000);
        snapshot.DriveState.SpeedFloat = 49.6f;

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(80.4672m, drive.Speed); // 50 mph
    }

    [Fact]
    public void WhenSnapshotHasWholeAndFractionalSpeed_ThenWholeSpeedWins()
    {
        // Arrange
        var drive = CreateVehicleFromDriveFixture().Drive;
        var snapshot = CreateSnapshot(60_000);
        snapshot.DriveState.Speed = 30;
        snapshot.DriveState.SpeedFloat = 30.4f;

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(48.28032m, drive.Speed); // 30 mph
    }

    [Fact]
    public void WhenSnapshotHasNoRoute_ThenNavigationIsCleared()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        drive.Update(new TeslaDriveStateDto { ActiveRouteDestination = "Test Destination", ActiveRouteMinutesToArrival = 12m, Timestamp = FixtureTimestamp });

        // Act
        drive.Update(CreateSnapshot(60_000).DriveState);

        // Assert
        Assert.Null(drive.Navigation.Destination);
        Assert.Null(drive.Navigation.TimeToArrival);
    }

    [Fact]
    public void WhenDriveSnapshotHasRoute_ThenNavigationIsMapped()
    {
        // Arrange
        var drive = CreateVehicleFromDriveFixture().Drive;
        var snapshot = CreateSnapshot(60_000);
        snapshot.DriveState.ActiveRouteDestination = "Test Destination";
        snapshot.DriveState.ActiveRouteMinutesToArrival = 12.5f;
        snapshot.DriveState.ActiveRouteMilesToArrival = 10f;
        snapshot.DriveState.ActiveRouteEnergyAtArrival = 40f;
        snapshot.DriveState.ActiveRouteTrafficMinutesDelay = 3f;
        snapshot.DriveState.ActiveRouteCoordinates = new LatLong { Latitude = 47.25f, Longitude = 8.25f };

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal("Test Destination", drive.Navigation.Destination);
        Assert.Equal(new GeoCoordinate(47.25, 8.25), drive.Navigation.DestinationLocation);
        Assert.Equal(TimeSpan.FromMinutes(12.5), drive.Navigation.TimeToArrival);
        Assert.Equal(16.09344m, drive.Navigation.DistanceToArrival);
        Assert.Equal(0.4m, drive.Navigation.EnergyAtArrival);
        Assert.Equal(TimeSpan.FromMinutes(3), drive.Navigation.TrafficDelay);
    }

    [Fact]
    public void WhenDriveSnapshotEndsRoute_ThenNavigationIsCleared()
    {
        // Arrange
        var drive = CreateVehicle().Drive;
        var routeSnapshot = CreateSnapshot(0);
        routeSnapshot.DriveState.ActiveRouteDestination = "Test Destination";
        routeSnapshot.DriveState.ActiveRouteMinutesToArrival = 12f;
        drive.Update(routeSnapshot.DriveState);
        var endSnapshot = CreateSnapshot(60_000);
        endSnapshot.DriveState.ActiveRouteDestination = string.Empty;

        // Act
        drive.Update(endSnapshot.DriveState);

        // Assert
        Assert.Null(drive.Navigation.Destination);
        Assert.Null(drive.Navigation.TimeToArrival);
    }

    [Fact]
    public void WhenDriveSnapshotIsOlder_ThenItIsIgnored()
    {
        // Arrange
        var drive = CreateVehicleFromDriveFixture().Drive;
        var snapshot = CreateSnapshot(-60_000);

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(TeslaShiftState.Park, drive.ShiftState);
        Assert.Null(drive.Speed);
    }

    [Fact]
    public void WhenDriveSnapshotHasEqualTimestamp_ThenItApplies()
    {
        // Arrange
        var drive = CreateVehicleFromDriveFixture().Drive;
        var snapshot = CreateSnapshot(0);

        // Act
        drive.Update(snapshot.DriveState);

        // Assert
        Assert.Equal(TeslaShiftState.Drive, drive.ShiftState);
    }

    [Fact]
    public void WhenJsonDriveIsOlderThanAppliedSnapshot_ThenOnlyTheNewerSectionsAreIgnored()
    {
        // Arrange
        var vehicle = CreateVehicle();
        var drive = vehicle.Drive;
        var snapshot = CreateSnapshot(60_000);
        drive.Update(snapshot.LocationState);

        // Act
        drive.Update(LoadFixture().DriveState!);

        // Assert
        Assert.Equal(47.5, vehicle.Location!.Value.Latitude, 3); // location snapshot is newer than the JSON
        Assert.Equal(TeslaShiftState.Park, drive.ShiftState); // the drive section had no newer data
    }
}
