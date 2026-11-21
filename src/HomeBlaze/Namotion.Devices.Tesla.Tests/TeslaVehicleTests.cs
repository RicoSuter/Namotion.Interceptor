using System.Reactive.Concurrency;
using System.Text.Json;
using CarServer;
using Google.Protobuf.WellKnownTypes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Devices.Vehicles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaVehicleTests
{
    private const long FixtureTimestamp = 1795293677353;
    private const string Vin = "TESTVIN0000000001";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1795293700000);

    private static TeslaVehicle Create()
    {
        var time = new FakeTimeProvider(Now);
        var products = JsonSerializer.Deserialize(FixtureLoader.Load("products.json"), TeslaJsonContext.Default.TeslaResponseTeslaProductDtoArray)!.Response!;
        var vehicle = new TeslaVehicle(products[0].Id, Vin, time, NullLogger.Instance);
        vehicle.ApplyProduct(products[0]);
        return vehicle;
    }

    private static TeslaVehicleDataDto LoadVehicleData() =>
        JsonSerializer.Deserialize(FixtureLoader.Load("vehicle-data.json"), TeslaJsonContext.Default.TeslaResponseTeslaVehicleDataDto)!.Response!;

    private static TeslaProductDto CachedProduct(TeslaVehicle vehicle, VehicleData snapshot) => new()
    {
        Id = vehicle.Id, VehicleId = 2, Vin = Vin, State = "asleep", CachedData = VehicleDataBuilder.ToBase64(snapshot)
    };

    [Fact]
    public void WhenProductIsApplied_ThenIdentityAndStateAreSet()
    {
        // Act
        var vehicle = Create();

        // Assert
        Assert.Equal(Vin, vehicle.Vin);
        Assert.Equal(Vin, vehicle.SerialNumber);
        Assert.Equal(TeslaVehicleState.Online, vehicle.VehicleState);
        Assert.True(vehicle.IsAwake);
        Assert.True(vehicle.IsConnected);
        Assert.Equal(TeslaCommandSigning.Required, vehicle.CommandSigning);
        Assert.False(vehicle.IsInService);
        Assert.Null(vehicle.DisplayName); // empty in the fixture
        Assert.Equal("Tesla", vehicle.Manufacturer);
    }

    [Theory]
    [InlineData("online", true, true)]
    [InlineData("asleep", false, true)]
    [InlineData("offline", false, false)]
    public void WhenVehicleStateChanges_ThenAwakeAndConnectedFollow(string state, bool expectedAwake, bool expectedConnected)
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyProduct(new TeslaProductDto { Id = vehicle.Id, VehicleId = 2, State = state });

        // Assert
        Assert.Equal(expectedAwake, vehicle.IsAwake);
        Assert.Equal(expectedConnected, vehicle.IsConnected);
    }

    [Fact]
    public void WhenVehicleStateIsUnknown_ThenAwakeIsUnknownAndNotConnected()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyProduct(new TeslaProductDto { Id = vehicle.Id, VehicleId = 2, State = "parked on the moon" });

        // Assert
        Assert.Equal(TeslaVehicleState.Unknown, vehicle.VehicleState);
        Assert.Null(vehicle.IsAwake);
        Assert.False(vehicle.IsConnected);
    }

    [Fact]
    public void WhenVehicleDataIsApplied_ThenSharedInterfacesAreDerived()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(LoadVehicleData());

        // Assert
        IElectricVehicle electricVehicle = vehicle;
        Assert.Equal(0.7m, electricVehicle.BatteryLevel);
        Assert.Equal(0.7m, electricVehicle.ChargeLimit);
        Assert.Equal(vehicle.Charge.Range, electricVehicle.Range);
        Assert.True(electricVehicle.IsPluggedIn);
        Assert.False(electricVehicle.IsCharging);
        Assert.Equal(0m, electricVehicle.ChargingPower);
        Assert.Null(electricVehicle.MaximumChargingPower); // no charger voltage while not charging
        Assert.Null(electricVehicle.RemainingChargingTime);
        Assert.Equal(36469.00m * 1.609344m, electricVehicle.TotalDrivenDistance); // miles rounded to the hundredths of the snapshot
        Assert.Equal(new GeoCoordinate(47.0, 8.0), electricVehicle.Location);
        Assert.Equal(12.1m, ((IVehicleClimateState)vehicle).InsideTemperature);
        Assert.Equal(vehicle.Climate.IsClimateOn, vehicle.IsClimateOn);
        Assert.Equal(vehicle.Climate.OutsideTemperature, vehicle.OutsideTemperature);
        Assert.Equal(DoorLockState.Locked, vehicle.LockState);
        Assert.Equal("2026.38.6", vehicle.SoftwareVersion);
        Assert.Null(vehicle.AvailableSoftwareUpdate);
        Assert.Equal("Model 3", vehicle.Model);
        Assert.Equal("74d", vehicle.ProductCode);
        Assert.Null(vehicle.VehicleName); // empty in the fixture
        Assert.Equal("Model 3 000001", vehicle.Title); // no vehicle name and no display name
        Assert.Equal(Now, vehicle.LastUpdated);
    }

    [Fact]
    public void WhenVehicleDataIsApplied_ThenChildSectionsAreUpdated()
    {
        // Arrange
        var vehicle = Create();
        var data = LoadVehicleData();

        // Act
        vehicle.ApplyVehicleData(data);

        // Assert
        Assert.NotNull(vehicle.DisplaySettings.DistanceUnits);
        Assert.Equal("model3", vehicle.Specification.CarType);
        Assert.Equal(TeslaMediaPlaybackStatus.Paused, vehicle.Media.PlaybackStatus);
        Assert.NotNull(vehicle.SpeedLimit.IsActive);
        Assert.NotNull(vehicle.Body.FrontLeftTire.Pressure);
    }

    [Fact]
    public void WhenVehicleStateHasNameAndHomelink_ThenTheVehicleAppliesThem()
    {
        // Arrange
        var vehicle = Create();
        var data = new TeslaVehicleDataDto
        {
            State = "online",
            VehicleState = new TeslaVehicleStateDto { Timestamp = FixtureTimestamp, VehicleName = "Red Car", HomelinkNearby = true, CarVersion = " " }
        };

        // Act
        vehicle.ApplyVehicleData(data);

        // Assert
        Assert.Equal("Red Car", vehicle.VehicleName);
        Assert.Equal("Red Car", vehicle.Title);
        Assert.True(vehicle.Drive.IsHomelinkNearby);
        Assert.Null(vehicle.SoftwareVersion); // blank placeholder
    }

    [Fact]
    public void WhenOnlyDisplayNameIsSet_ThenItIsTheTitle()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyProduct(new TeslaProductDto { Id = vehicle.Id, VehicleId = 2, State = "online", DisplayName = " Garage Car " });

        // Assert
        Assert.Equal("Garage Car", vehicle.Title);
        Assert.Equal("Tesla 000001", new TeslaVehicle(1, Vin, TimeProvider.System, NullLogger.Instance).Title);
    }

    [Fact]
    public void WhenCachedDataIsApplied_ThenValuesAppearWithoutVehicleData()
    {
        // Arrange
        var vehicle = Create();
        var snapshot = VehicleDataBuilder.Create(Now);

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, snapshot));

        // Assert
        Assert.Equal(TeslaVehicleState.Asleep, vehicle.VehicleState);
        Assert.Equal(0.65m, vehicle.BatteryLevel);
        Assert.True(vehicle.IsCharging);
        Assert.True(vehicle.IsClimateOn);
        Assert.Equal(DoorLockState.Unlocked, vehicle.LockState);
        Assert.Equal(new GeoCoordinate(47.5, 8.5), vehicle.Location);
        Assert.Equal(Now, vehicle.LastUpdated);
    }

    [Fact]
    public void WhenCachedDataHasOdometerAndSpeedLimit_ThenTheyAreApplied()
    {
        // Arrange
        var vehicle = Create();
        var snapshot = VehicleDataBuilder.Create(Now);
        snapshot.DriveState.OdometerInHundredthsOfAMile = 3646900;
        snapshot.ClosuresState.SpeedLimitMode = new SpeedLimitMode { Active = true, PinCodeSet = false };

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, snapshot));

        // Assert
        Assert.Equal(36469.00m * 1.609344m, vehicle.TotalDrivenDistance);
        Assert.True(vehicle.SpeedLimit.IsActive);
        Assert.False(vehicle.SpeedLimit.IsPinCodeSet);
    }

    [Fact]
    public void WhenJsonAndCachedOdometerDescribeTheSameState_ThenTheDistancesAreEqual()
    {
        // Arrange
        var fromJson = Create();
        var fromCache = Create();
        var snapshot = new VehicleData
        {
            DriveState = new DriveState { OdometerInHundredthsOfAMile = 3646900, Timestamp = Timestamp.FromDateTimeOffset(Now) }
        };

        // Act
        fromJson.ApplyVehicleData(LoadVehicleData());
        fromCache.ApplyProduct(CachedProduct(fromCache, snapshot));

        // Assert
        Assert.Equal(fromJson.TotalDrivenDistance, fromCache.TotalDrivenDistance);
    }

    [Fact]
    public void WhenCachedOdometerIsOlderThanJson_ThenItIsIgnored()
    {
        // Arrange
        var vehicle = Create();
        vehicle.ApplyVehicleData(LoadVehicleData());
        var olderTime = DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp - 60000);
        var snapshot = new VehicleData
        {
            DriveState = new DriveState { OdometerInHundredthsOfAMile = 100, Timestamp = Timestamp.FromDateTimeOffset(olderTime) }
        };

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, snapshot));

        // Assert
        Assert.Equal(36469.00m * 1.609344m, vehicle.TotalDrivenDistance);
        Assert.Equal(Now, vehicle.LastUpdated);
    }

    [Fact]
    public void WhenCachedDataHasNoSections_ThenNothingChanges()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, new VehicleData { ClosuresState = new ClosuresState() }));

        // Assert
        Assert.Null(vehicle.BatteryLevel);
        Assert.Null(vehicle.LockState);
        Assert.Null(vehicle.LastUpdated);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AQID")] // valid base64, but not a protobuf message
    public void WhenCachedDataIsInvalid_ThenItIsIgnored(string cachedData)
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyProduct(new TeslaProductDto { Id = vehicle.Id, VehicleId = 2, State = "asleep", CachedData = cachedData });

        // Assert
        Assert.Equal(TeslaVehicleState.Asleep, vehicle.VehicleState);
        Assert.Null(vehicle.BatteryLevel);
    }

    [Fact]
    public void WhenChildChanges_ThenDerivedInterfacePropertyChangeIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var vehicle = Create();
        ((IInterceptorSubject)vehicle).Context.AddFallbackContext(context);
        var changes = new List<SubjectPropertyChange>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, VehicleDataBuilder.Create(Now)));

        // Assert
        Assert.Contains(changes, change => change.Property.Subject == vehicle && change.Property.Name == nameof(TeslaVehicle.BatteryLevel));
        Assert.Contains(changes, change => change.Property.Subject == vehicle && change.Property.Name == nameof(TeslaVehicle.IconColor));
        Assert.Equal(0.65m, vehicle.BatteryLevel);
    }

    [Fact]
    public void WhenVehicleIsAsleep_ThenWakeUpIsEnabledOnlyWithAHandler()
    {
        // Arrange
        var vehicle = Create();
        vehicle.ApplyProduct(new TeslaProductDto { Id = vehicle.Id, VehicleId = 2, State = "asleep" });
        var isEnabledWithoutHandler = vehicle.WakeUp_IsEnabled;

        // Act
        vehicle.SetWakeUpHandler((_, _) => Task.CompletedTask);

        // Assert
        Assert.False(isEnabledWithoutHandler);
        Assert.True(vehicle.WakeUp_IsEnabled);
    }

    [Fact]
    public async Task WhenWakeUpIsCalled_ThenTheHandlerIsInvoked()
    {
        // Arrange
        var vehicle = Create();
        TeslaVehicle? wokenVehicle = null;
        vehicle.SetWakeUpHandler((target, _) =>
        {
            wokenVehicle = target;
            return Task.CompletedTask;
        });

        // Act
        await vehicle.WakeUpAsync(CancellationToken.None);

        // Assert
        Assert.Same(vehicle, wokenVehicle);
        Assert.False(vehicle.WakeUp_IsEnabled); // online
    }

    [Fact]
    public void WhenVehicleCharges_ThenItIsActiveAndCannotSleep()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, VehicleDataBuilder.Create(Now)));

        // Assert
        Assert.True(vehicle.IsActive);
        Assert.False(vehicle.CanSleep);
    }

    [Fact]
    public void WhenVehicleIsParkedWithEverythingOff_ThenItCanSleep()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(LoadVehicleData());

        // Assert
        Assert.False(vehicle.IsActive);
        Assert.True(vehicle.CanSleep);
    }
}
