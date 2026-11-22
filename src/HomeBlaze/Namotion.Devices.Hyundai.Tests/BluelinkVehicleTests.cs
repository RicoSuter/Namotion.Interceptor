using System.Text.Json;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Devices.Vehicles;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Devices.Hyundai.Tests.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkVehicleTests
{
    private static readonly DateTimeOffset ReadTime = new(2026, 11, 22, 10, 20, 0, TimeSpan.Zero);

    private static (BluelinkVehicle Vehicle, FakeVehicleCommands Commands) Create(string? nickname = "Kona")
    {
        var commands = new FakeVehicleCommands();
        var vehicle = new BluelinkVehicle(commands, new BluelinkVehicleInfo("VEHICLE-1", "TESTVIN0000000001", nickname, "KONA", "EV", BluelinkProtocol.Legacy));
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
    public void WhenNicknameIsMissing_ThenTitleShowsTheVinSuffix()
    {
        // Act
        var (vehicle, _) = Create(nickname: null);

        // Assert
        Assert.Equal("Hyundai: 000001", vehicle.Title);
    }

    [Fact]
    public void WhenVehicleIsUsedThroughInterfaces_ThenTheChildrenAreReached()
    {
        // Arrange
        var (vehicle, _) = Create();
        IElectricVehicle electricVehicle = vehicle;

        // Act & Assert
        Assert.Same(vehicle.Charge, electricVehicle.Charge);
        Assert.Same(vehicle.Climate, electricVehicle.Climate);
        Assert.Same(vehicle.Body, electricVehicle.Body);
        Assert.IsAssignableFrom<IVehicleChargingController>(electricVehicle.Charge);
        Assert.IsAssignableFrom<IVehicleClimateController>(electricVehicle.Climate);
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
        await vehicle.Climate.StartClimateAsync(21m, CancellationToken.None);

        // Assert
        Assert.Equal(["Climate 21 True", "Climate 21 False"], commands.Calls);
    }

    [Fact]
    public void WhenAuxiliaryBatteryIsLow_ThenRefreshIsDisabled()
    {
        // Arrange
        var (vehicle, _) = Create();

        // Act
        vehicle.ApplyStatus(FixtureStatus() with { AuxiliaryBatteryLevel = 0.5m }, ReadTime);

        // Assert
        Assert.False(vehicle.Refresh_IsEnabled);
    }
}
