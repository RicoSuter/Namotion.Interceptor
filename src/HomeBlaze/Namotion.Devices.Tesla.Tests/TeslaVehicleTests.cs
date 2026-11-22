using System.Reactive.Concurrency;
using System.Text.Json;
using CarServer;
using Google.Protobuf.WellKnownTypes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Devices.Vehicles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Tests.Testing;
using static Namotion.Devices.Tesla.Tests.Testing.FixtureLoader;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaVehicleTests
{
    private const string Vin = "TESTVIN0000000001";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1795293700000);
    private static readonly DateTimeOffset FixtureTime = DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp);

    private static TeslaVehicle Create()
    {
        var time = new FakeTimeProvider(Now);
        var products = JsonSerializer.Deserialize(FixtureLoader.Load("products.json"), TeslaJsonContext.Default.TeslaResponseTeslaProductDtoArray)!.Response!;
        var vehicle = new TeslaVehicle(products[0].Id, Vin, time, NullLogger.Instance);
        vehicle.ApplyProduct(products[0]);
        return vehicle;
    }

    private static TeslaVehicleDataDto ChargeData(string chargingState, int power, int current, int voltage, int phases, int pilotCurrent, int maximumCurrent) => new()
    {
        State = "online",
        ChargeState = new TeslaChargeStateDto
        {
            Timestamp = FixtureTimestamp,
            ChargingState = chargingState,
            ChargerPower = power,
            ChargerActualCurrent = current,
            ChargerVoltage = voltage,
            ChargerPhases = phases,
            ChargerPilotCurrent = pilotCurrent,
            ChargeCurrentRequestMax = maximumCurrent
        }
    };

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
    public void WhenVehicleDataIsApplied_ThenSharedInterfacesNavigateToTheChildren()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(LoadVehicleData());

        // Assert
        IElectricVehicle electricVehicle = vehicle;
        Assert.Same(vehicle.Charge, electricVehicle.Charge);
        Assert.Same(vehicle.Climate, electricVehicle.Climate);
        Assert.Same(vehicle.Body, electricVehicle.Body);
        Assert.Equal(0.7m, electricVehicle.Charge.BatteryLevel);
        Assert.Equal(0.7m, electricVehicle.Charge.ChargeLimit);
        Assert.NotNull(electricVehicle.Charge.Range);
        Assert.True(electricVehicle.Charge.IsPluggedIn);
        Assert.False(electricVehicle.Charge.IsCharging);
        Assert.Equal(0m, electricVehicle.Charge.ChargingPower);
        Assert.Null(electricVehicle.Charge.MaximumChargingPower); // no charger voltage while not charging
        Assert.Null(electricVehicle.Charge.RemainingChargingTime);
        Assert.Equal(36469.00m * 1.609344m, electricVehicle.TotalDrivenDistance); // miles rounded to the hundredths of the snapshot
        Assert.Equal(new GeoCoordinate(47.0, 8.0), electricVehicle.Location);
        Assert.Equal(12.1m, electricVehicle.Climate!.InsideTemperature);
        Assert.Equal(DoorLockState.Locked, electricVehicle.Body!.LockState);
        Assert.Equal("2026.38.6", vehicle.SoftwareVersion);
        Assert.Null(vehicle.AvailableSoftwareUpdate);
        Assert.Equal("Model 3", vehicle.Model);
        Assert.Equal("74d", vehicle.ProductCode);
        Assert.Null(vehicle.VehicleName); // empty in the fixture
        Assert.Equal("Model 3: 000001", vehicle.Title); // no vehicle name and no display name
        Assert.Equal(FixtureTime, vehicle.LastUpdated); // the newest section timestamp, not the read time
    }

    [Theory]
    [InlineData(nameof(TeslaVehicle.Charge), typeof(TeslaChargeState))]
    [InlineData(nameof(TeslaVehicle.Climate), typeof(TeslaClimateState))]
    [InlineData(nameof(TeslaVehicle.Body), typeof(TeslaVehicleBody))]
    public void WhenInterfaceNavigationIsImplementedExplicitly_ThenTheMetadataHasOnlyTheTrackedChild(string name, System.Type childType)
    {
        // Arrange
        var vehicle = Create();

        // Act
        var properties = ((IInterceptorSubject)vehicle).Properties;

        // Assert
        var property = Assert.Single(properties.Values, property => property.Name == name);
        Assert.Equal(childType, property.Type);
        Assert.True(property.IsIntercepted);
        Assert.False(property.IsDerived);
        Assert.Same(typeof(TeslaVehicle).GetProperty(name)!.GetValue(vehicle), property.GetValue!(vehicle));
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
        Assert.Equal("Tesla: 000001", new TeslaVehicle(1, Vin, TimeProvider.System, NullLogger.Instance).Title);
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
        Assert.Equal(0.65m, vehicle.Charge.BatteryLevel);
        Assert.True(vehicle.Charge.IsCharging);
        Assert.True(vehicle.Climate.IsClimateOn);
        Assert.Equal(DoorLockState.Unlocked, vehicle.Body.LockState);
        Assert.Equal(new GeoCoordinate(47.5, 8.5), vehicle.Location);
        Assert.Equal(Now, vehicle.LastUpdated);
    }

    [Fact]
    public void WhenTheSameCachedDataIsAppliedAgain_ThenItIsNotParsedAgain()
    {
        // Arrange
        var vehicle = Create();
        var product = CachedProduct(vehicle, VehicleDataBuilder.Create(Now));
        vehicle.ApplyProduct(product);

        // A snapshot with an equal timestamp applies, so only a skipped parse keeps this value.
        vehicle.Charge.BatteryLevel = 0.5m;

        // Act
        vehicle.ApplyProduct(product);

        // Assert
        Assert.Equal(0.5m, vehicle.Charge.BatteryLevel);
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
        Assert.Equal(FixtureTime, vehicle.LastUpdated);
    }

    [Fact]
    public void WhenCachedDataHasNoSections_ThenNothingChanges()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, new VehicleData { ClosuresState = new ClosuresState() }));

        // Assert
        Assert.Null(vehicle.Charge.BatteryLevel);
        Assert.Null(vehicle.Body.LockState);
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
        Assert.Null(vehicle.Charge.BatteryLevel);
    }

    [Fact]
    public void WhenChildChanges_ThenTheChildAndTheDerivedVehiclePropertyPublishChanges()
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
        Assert.Contains(changes, change => change.Property.Subject == vehicle.Charge && change.Property.Name == nameof(TeslaChargeState.BatteryLevel));
        Assert.Contains(changes, change => change.Property.Subject == vehicle && change.Property.Name == nameof(TeslaVehicle.IconColor));
        Assert.Equal("Success", vehicle.IconColor);
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
    [Fact]
    public void WhenThreePhaseChargingIsReportedAsTwoPhases_ThenMaximumPowerUsesThreePhases()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(ChargeData("Charging", power: 11, current: 16, voltage: 230, phases: 2, pilotCurrent: 16, maximumCurrent: 16));

        // Assert
        Assert.Equal(230m * 16m * 3m, vehicle.Charge.MaximumChargingPower);
    }

    [Fact]
    public void WhenChargingOnOnePhase_ThenMaximumPowerUsesOnePhase()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(ChargeData("Charging", power: 7, current: 32, voltage: 230, phases: 1, pilotCurrent: 32, maximumCurrent: 32));

        // Assert
        Assert.Equal(230m * 32m, vehicle.Charge.MaximumChargingPower);
    }

    [Fact]
    public void WhenPilotCurrentIsBelowTheVehicleLimit_ThenMaximumPowerUsesThePilotCurrent()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(ChargeData("Charging", power: 7, current: 10, voltage: 230, phases: 2, pilotCurrent: 10, maximumCurrent: 16));

        // Assert
        Assert.Equal(230m * 10m * 3m, vehicle.Charge.MaximumChargingPower);
    }

    [Fact]
    public void WhenNoPowerIsMeasured_ThenMaximumPowerFallsBackToTheReportedPhases()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(ChargeData("Starting", power: 0, current: 0, voltage: 230, phases: 2, pilotCurrent: 16, maximumCurrent: 16));

        // Assert
        Assert.Equal(230m * 16m * 2m, vehicle.Charge.MaximumChargingPower);
    }

    [Fact]
    public void WhenOnlyClimateIsOn_ThenVehicleIsNotActiveButCannotSleep()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(new TeslaVehicleDataDto { State = "online", ClimateState = new TeslaClimateStateDto { Timestamp = FixtureTimestamp, IsClimateOn = true } });

        // Assert
        Assert.False(vehicle.IsActive);
        Assert.False(vehicle.CanSleep);
    }

    [Fact]
    public void WhenOnlySentryModeIsOn_ThenVehicleIsNotActiveButCannotSleep()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(new TeslaVehicleDataDto { State = "online", VehicleState = new TeslaVehicleStateDto { Timestamp = FixtureTimestamp, SentryMode = true } });

        // Assert
        Assert.False(vehicle.IsActive);
        Assert.False(vehicle.CanSleep);
    }

    [Fact]
    public void WhenVehicleDrives_ThenItIsActive()
    {
        // Arrange
        var vehicle = Create();
        var snapshot = new VehicleData
        {
            DriveState = new DriveState { ShiftState = new ShiftState { D = new CarServer.Void() }, SpeedFloat = 30f, Timestamp = Timestamp.FromDateTimeOffset(Now) }
        };

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, snapshot));

        // Assert
        Assert.True(vehicle.IsActive);
        Assert.False(vehicle.CanSleep);
    }

    [Fact]
    public void WhenUserIsPresent_ThenItIsActive()
    {
        // Arrange
        var vehicle = Create();
        var snapshot = new VehicleData { ClosuresState = new ClosuresState { IsUserPresent = true, Timestamp = Timestamp.FromDateTimeOffset(Now) } };

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, snapshot));

        // Assert
        Assert.True(vehicle.IsActive);
        Assert.False(vehicle.CanSleep);
    }

    [Fact]
    public void WhenSnapshotsArriveInReverseOrder_ThenLastUpdatedKeepsTheNewest()
    {
        // Arrange
        var vehicle = Create();
        vehicle.ApplyProduct(CachedProduct(vehicle, VehicleDataBuilder.Create(Now)));

        // Act
        vehicle.ApplyProduct(CachedProduct(vehicle, VehicleDataBuilder.Create(Now.AddMinutes(-5))));

        // Assert
        Assert.Equal(Now, vehicle.LastUpdated);
    }

    [Fact]
    public void WhenVehicleDataHasNoSectionTimestamp_ThenLastUpdatedIsTheReadTime()
    {
        // Arrange
        var vehicle = Create();

        // Act
        vehicle.ApplyVehicleData(new TeslaVehicleDataDto { State = "online", ClimateState = new TeslaClimateStateDto { IsClimateOn = false } });

        // Assert
        Assert.Equal(Now, vehicle.LastUpdated);
    }

    [Fact]
    public void WhenJsonVehicleStateIsOlderThanTheSnapshotLocation_ThenHomelinkKeepsTheSnapshotValue()
    {
        // Arrange
        var vehicle = Create();
        var snapshot = new VehicleData { LocationState = new LocationState { HomelinkNearby = true, Timestamp = Timestamp.FromDateTimeOffset(Now) } };
        vehicle.ApplyProduct(CachedProduct(vehicle, snapshot));

        // Act
        vehicle.ApplyVehicleData(new TeslaVehicleDataDto
        {
            State = "online",
            VehicleState = new TeslaVehicleStateDto { Timestamp = FixtureTimestamp, HomelinkNearby = false }
        });

        // Assert
        Assert.True(vehicle.Drive.IsHomelinkNearby);
    }

    [Fact]
    public void WhenCachedDataStaysInvalid_ThenOnlyTheFirstFailureIsAWarning()
    {
        // Arrange
        var logger = new RecordingLogger();
        var vehicle = new TeslaVehicle(1, Vin, new FakeTimeProvider(Now), logger);
        var invalid = new TeslaProductDto { Id = 1, State = "asleep", CachedData = "not base64!" };

        // Act
        vehicle.ApplyProduct(invalid);
        vehicle.ApplyProduct(invalid);
        vehicle.ApplyProduct(CachedProduct(vehicle, VehicleDataBuilder.Create(Now)));
        vehicle.ApplyProduct(invalid);

        // Assert
        Assert.Equal([LogLevel.Warning, LogLevel.Debug, LogLevel.Warning], logger.Levels);
    }

    [Fact]
    public void WhenVehicleFallsAsleep_ThenAwakeChangeIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var vehicle = Create();
        ((IInterceptorSubject)vehicle).Context.AddFallbackContext(context);
        var changes = new List<SubjectPropertyChange>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        vehicle.ApplyProduct(new TeslaProductDto { Id = vehicle.Id, VehicleId = 2, State = "asleep" });

        // Assert
        var change = Assert.Single(changes, change => change.Property.Subject == vehicle && change.Property.Name == nameof(TeslaVehicle.IsAwake));
        Assert.False(change.GetNewValue<bool?>());
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
