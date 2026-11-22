using System.ComponentModel;
using System.Text.Json;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Devices.Vehicles;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Devices.Hyundai.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkVehicleTests
{
    private static readonly DateTimeOffset ReadTime = new(2026, 11, 22, 10, 20, 0, TimeSpan.Zero);

    private static (BluelinkVehicle Vehicle, FakeVehicleCommands Commands) Create(string? nickname = "Kona", IInterceptorSubjectContext? context = null, string? model = "KONA")
    {
        var commands = context is null ? new FakeVehicleCommands() : new FakeVehicleCommands(context);
        var vehicle = new BluelinkVehicle(commands, new BluelinkVehicleInfo("VEHICLE-1", "TESTVIN0000000001", nickname, model, "EV", BluelinkProtocol.Legacy));
        return (vehicle, commands);
    }

    private static BluelinkVehicleStatus FixtureStatus()
    {
        using var document = JsonDocument.Parse(FixtureLoader.Load("carstatus-legacy-ev.json"));
        return BluelinkStatusParser.Parse(document.RootElement.Find("resMsg.state.Vehicle")!.Value);
    }

    [Fact]
    public void WhenStatusIsApplied_ThenVehicleAndChildrenAreUpdated()
    {
        // Arrange
        var (vehicle, _) = Create();

        // Act
        vehicle.ApplyStatus(FixtureStatus(), ReadTime);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 15, 0, TimeSpan.Zero), vehicle.LastUpdated);
        Assert.Equal(16773.9m, vehicle.TotalDrivenDistance);
        Assert.Equal(0.84m, vehicle.AuxiliaryBatteryLevel);
        Assert.Equal(0.465m, vehicle.Charge.BatteryLevel);
        Assert.True(vehicle.Charge.IsCharging);
        Assert.Equal(1.0m, vehicle.Charge.FastChargeLimit);
        Assert.False(vehicle.Climate.IsClimateOn);
        Assert.Equal(DoorLockState.Locked, vehicle.Body.LockState);
        Assert.True(vehicle.Body.IsAnyDoorOpen);
        Assert.True(vehicle.Body.FrontRightTire.IsPressureLow);
    }

    [Fact]
    public void WhenOlderStatusArrives_ThenItIsSkipped()
    {
        // Arrange
        var (vehicle, _) = Create();
        vehicle.ApplyStatus(FixtureStatus(), ReadTime);

        // Act
        vehicle.ApplyStatus(FixtureStatus() with { Time = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero), BatteryLevel = 0.1m }, ReadTime);

        // Assert
        Assert.Equal(0.465m, vehicle.Charge.BatteryLevel);
    }

    [Fact]
    public void WhenStatusHasNoTime_ThenReadTimeIsLastUpdated()
    {
        // Arrange
        var (vehicle, _) = Create();

        // Act
        vehicle.ApplyStatus(new BluelinkVehicleStatus(), ReadTime);

        // Assert
        Assert.Equal(ReadTime, vehicle.LastUpdated);
    }

    [Fact]
    public void WhenNicknameIsMissing_ThenTitleShowsTheModelAndTheVinSuffix()
    {
        // Act
        var (vehicle, _) = Create(nickname: null);

        // Assert
        Assert.Equal("KONA: 000001", vehicle.Title);
        Assert.Equal("KONA", vehicle.Model);
    }

    [Fact]
    public void WhenNicknameAndModelAreMissing_ThenTitleShowsTheBrandAndTheVinSuffix()
    {
        // Act
        var (vehicle, _) = Create(nickname: null, model: null);

        // Assert
        Assert.Equal("Hyundai: 000001", vehicle.Title);
    }

    [Fact]
    public void WhenNicknameIsSet_ThenTitleIsTheNickname()
    {
        // Act
        var (vehicle, _) = Create();

        // Assert
        Assert.Equal("Kona", vehicle.Title);
    }

    [Fact]
    public void WhenVehicleIsUsedThroughInterfaces_ThenTheChildrenAreReached()
    {
        // Arrange
        var (vehicle, _) = Create();
        IElectricVehicle electricVehicle = vehicle;

        // Act
        var charge = electricVehicle.Charge;
        var climate = electricVehicle.Climate;
        var body = electricVehicle.Body;

        // Assert
        Assert.Same(vehicle.Charge, charge);
        Assert.Same(vehicle.Climate, climate);
        Assert.Same(vehicle.Body, body);
        Assert.IsAssignableFrom<IVehicleChargingController>(charge);
        Assert.IsAssignableFrom<IVehicleClimateController>(climate);
    }

    [Fact]
    public async Task WhenChargeLimitIsSet_ThenTheCurrentFastChargeLimitIsSentAlong()
    {
        // Arrange
        var (vehicle, commands) = Create();
        vehicle.ApplyStatus(FixtureStatus(), ReadTime);

        // Act
        await vehicle.Charge.SetChargeLimitAsync(0.7m, CancellationToken.None);

        // Assert
        Assert.Equal(["Limits 70 100"], commands.Calls);
    }

    [Fact]
    public async Task WhenFastChargeLimitIsUnknown_ThenSetChargeLimitThrowsBeforeAnyCall()
    {
        // Arrange
        var (vehicle, commands) = Create();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => vehicle.Charge.SetChargeLimitAsync(0.7m, CancellationToken.None));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task WhenChargeLimitIsNotASetting_ThenItThrowsBeforeAnyCall()
    {
        // Arrange
        var (vehicle, commands) = Create();
        vehicle.ApplyStatus(FixtureStatus(), ReadTime);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => vehicle.Charge.SetChargeLimitsAsync(0.75m, 1.0m, CancellationToken.None));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task WhenTemperatureIsNotInTheTable_ThenStartClimateThrowsBeforeAnyCall()
    {
        // Arrange
        var (vehicle, commands) = Create();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => vehicle.Climate.StartClimateAsync(35m, CancellationToken.None));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task WhenClimateStartsWithDefrost_ThenDefrostIsRequested()
    {
        // Arrange
        var (vehicle, commands) = Create();

        // Act
        await vehicle.Climate.StartClimateWithDefrostAsync(21m, CancellationToken.None);

        // Assert
        Assert.Equal(["Climate 21 True"], commands.Calls);
    }

    [Fact]
    public async Task WhenClimateStartsWithoutDefrost_ThenDefrostIsNotRequested()
    {
        // Arrange
        var (vehicle, commands) = Create();

        // Act
        await vehicle.Climate.StartClimateAsync(21m, CancellationToken.None);

        // Assert
        Assert.Equal(["Climate 21 False"], commands.Calls);
    }

    [Fact]
    public async Task WhenClimateStops_ThenTheAccountIsAsked()
    {
        // Arrange
        var (vehicle, commands) = Create();

        // Act
        await vehicle.Climate.StopClimateAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Climate stop"], commands.Calls);
    }

    [Fact]
    public async Task WhenChargingStarts_ThenTheAccountIsAskedToStart()
    {
        // Arrange
        var (vehicle, commands) = Create();

        // Act
        await vehicle.Charge.StartChargingAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Charging True"], commands.Calls);
    }

    [Fact]
    public async Task WhenChargingStops_ThenTheAccountIsAskedToStop()
    {
        // Arrange
        var (vehicle, commands) = Create();

        // Act
        await vehicle.Charge.StopChargingAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Charging False"], commands.Calls);
    }

    [Fact]
    public async Task WhenBothChargeLimitsAreSet_ThenBothAreSentAsPercent()
    {
        // Arrange
        var (vehicle, commands) = Create();

        // Act
        await vehicle.Charge.SetChargeLimitsAsync(0.8m, 0.9m, CancellationToken.None);

        // Assert
        Assert.Equal(["Limits 80 90"], commands.Calls);
    }

    [Fact]
    public async Task WhenReportedFastChargeLimitIsNotASetting_ThenSetChargeLimitThrowsBeforeAnyCall()
    {
        // Arrange
        var (vehicle, commands) = Create();
        vehicle.ApplyStatus(FixtureStatus() with { FastChargeLimit = 0.75m }, ReadTime);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => vehicle.Charge.SetChargeLimitAsync(0.7m, CancellationToken.None));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public void WhenAllDoorsAreClosed_ThenNoDoorIsOpen()
    {
        // Arrange
        var (vehicle, _) = Create();

        // Act
        vehicle.ApplyStatus(new BluelinkVehicleStatus
        {
            IsDriverFrontDoorOpen = false,
            IsPassengerFrontDoorOpen = false,
            IsDriverRearDoorOpen = false,
            IsPassengerRearDoorOpen = false,
        }, ReadTime);

        // Assert
        Assert.False(vehicle.Body.IsAnyDoorOpen);
    }

    [Fact]
    public void WhenADoorIsUnknownAndNoneIsOpen_ThenAnyDoorOpenIsUnknown()
    {
        // Arrange
        var (vehicle, _) = Create();

        // Act
        vehicle.ApplyStatus(new BluelinkVehicleStatus
        {
            IsDriverFrontDoorOpen = false,
            IsPassengerFrontDoorOpen = null,
            IsDriverRearDoorOpen = false,
            IsPassengerRearDoorOpen = false,
        }, ReadTime);

        // Assert
        Assert.Null(vehicle.Body.IsAnyDoorOpen);
    }

    [Fact]
    public void WhenADoorIsOpenAndAnotherIsUnknown_ThenAnyDoorOpenIsTrue()
    {
        // Arrange
        var (vehicle, _) = Create();

        // Act
        vehicle.ApplyStatus(new BluelinkVehicleStatus
        {
            IsDriverFrontDoorOpen = null,
            IsPassengerFrontDoorOpen = true,
        }, ReadTime);

        // Assert
        Assert.True(vehicle.Body.IsAnyDoorOpen);
    }

    [Fact]
    public void WhenLocationIsMissing_ThenThePreviousLocationIsKept()
    {
        // Arrange
        var (vehicle, _) = Create();
        var location = new GeoCoordinate(1.234567, 2.345678);
        vehicle.ApplyLocation(location);

        // Act
        vehicle.ApplyLocation(null);

        // Assert
        Assert.Equal(location, vehicle.Location);
    }

    [Fact]
    public void WhenStatusWithoutTimeWasApplied_ThenAnOlderTimedStatusIsStillApplied()
    {
        // Arrange
        var (vehicle, _) = Create();
        vehicle.ApplyStatus(new BluelinkVehicleStatus { BatteryLevel = 0.2m }, ReadTime);

        // Act
        vehicle.ApplyStatus(new BluelinkVehicleStatus { Time = ReadTime.AddHours(-1), BatteryLevel = 0.3m }, ReadTime);

        // Assert
        Assert.Equal(0.3m, vehicle.Charge.BatteryLevel);
    }

    [Fact]
    public void WhenAccountCapabilityChanges_ThenOperationEnablementIsRaised()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var (vehicle, commands) = Create(context: context);
        ((IInterceptorSubject)vehicle).Context.AddFallbackContext(context);
        Assert.True(vehicle.Charge.StartCharging_IsEnabled);
        var firedEvents = new List<string>();
        ((INotifyPropertyChanged)vehicle.Charge).PropertyChanged += (_, arguments) => firedEvents.Add(arguments.PropertyName!);

        // Act
        commands.CanSend = false;

        // Assert
        Assert.False(vehicle.Charge.StartCharging_IsEnabled);
        Assert.Contains(nameof(BluelinkChargeState.StartCharging_IsEnabled), firedEvents);
    }

    [Fact]
    public void WhenVehicleBecomesBusy_ThenAllOperationsAreDisabledUntilItEnds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var (vehicle, _) = Create(context: context);
        ((IInterceptorSubject)vehicle).Context.AddFallbackContext(context);
        Assert.True(vehicle.Refresh_IsEnabled);
        var firedEvents = new List<string>();
        ((INotifyPropertyChanged)vehicle).PropertyChanged += (_, arguments) => firedEvents.Add(arguments.PropertyName!);
        ((INotifyPropertyChanged)vehicle.Charge).PropertyChanged += (_, arguments) => firedEvents.Add(arguments.PropertyName!);
        ((INotifyPropertyChanged)vehicle.Climate).PropertyChanged += (_, arguments) => firedEvents.Add(arguments.PropertyName!);

        // Act
        var started = vehicle.TryStartOperation();

        // Assert
        Assert.True(started);
        Assert.True(vehicle.IsBusy);
        Assert.False(vehicle.TryStartOperation());
        Assert.False(vehicle.Refresh_IsEnabled);
        Assert.False(vehicle.Charge.StartCharging_IsEnabled);
        Assert.False(vehicle.Charge.StopCharging_IsEnabled);
        Assert.False(vehicle.Charge.SetChargeLimit_IsEnabled);
        Assert.False(vehicle.Charge.SetChargeLimits_IsEnabled);
        Assert.False(vehicle.Climate.StartClimate_IsEnabled);
        Assert.False(vehicle.Climate.StartClimateWithDefrost_IsEnabled);
        Assert.False(vehicle.Climate.StopClimate_IsEnabled);
        Assert.Contains(nameof(BluelinkVehicle.Refresh_IsEnabled), firedEvents);
        Assert.Contains(nameof(BluelinkChargeState.StartCharging_IsEnabled), firedEvents);
        Assert.Contains(nameof(BluelinkClimateState.StopClimate_IsEnabled), firedEvents);

        vehicle.EndOperation();
        Assert.False(vehicle.IsBusy);
        Assert.True(vehicle.Refresh_IsEnabled);
        Assert.True(vehicle.Charge.StartCharging_IsEnabled);
        Assert.True(vehicle.Climate.StopClimate_IsEnabled);
        Assert.True(vehicle.TryStartOperation());
    }

    [Fact]
    public async Task WhenTwoOperationsStartAtTheSameTime_ThenExactlyOneProceeds()
    {
        // Arrange
        var (vehicle, _) = Create();
        using var barrier = new Barrier(2);
        bool StartAfterBarrier()
        {
            Assert.True(barrier.SignalAndWait(TestTimeouts.Default));
            return vehicle.TryStartOperation();
        }

        // Act
        var results = await Task.WhenAll(
            DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(StartAfterBarrier),
            DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(StartAfterBarrier));

        // Assert
        Assert.Single(results, started => started);
        Assert.True(vehicle.IsBusy);
    }

    [Fact]
    public void WhenVehicleIsRegistered_ThenItHasOneChargeChildAndTheCommandsAreNotAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var (vehicle, commands) = Create(context: context);

        // Act
        ((IInterceptorSubject)vehicle).Context.AddFallbackContext(context);
        var registered = vehicle.TryGetRegisteredSubject();

        // Assert
        Assert.NotNull(registered);
        var chargeProperty = Assert.Single(registered.Properties, property => property.Name == nameof(BluelinkVehicle.Charge));
        Assert.Single(chargeProperty.Children);
        Assert.Empty(commands.TryGetRegisteredSubject()!.Parents);
    }
}
