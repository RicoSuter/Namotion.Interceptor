using HomeBlaze.Samples;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

public class MotorTests
{
    private static Motor CreateMotor() => new(SubjectContextFactory.Create(new ServiceCollection()));

    [Fact]
    public void WhenMotorIsCreated_ThenItIsPoweredInNormalModeAtHalfLoad()
    {
        // Act
        var motor = CreateMotor();

        // Assert
        Assert.True(motor.IsPowered);
        Assert.Equal(MotorMode.Normal, motor.Mode);
        Assert.Equal(0.5, motor.Load);
    }

    [Fact]
    public void WhenPoweredOff_ThenSimulationSlowsDownAndKeepsTargetSpeed()
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = 1200;
        motor.CurrentSpeed = 1200;
        motor.Status = MotorStatus.Running;

        // Act
        motor.PowerOff();
        motor.SimulateStep();

        // Assert
        Assert.False(motor.IsPowered);
        Assert.Equal(1150, motor.CurrentSpeed);
        Assert.Equal(1200, motor.TargetSpeed);
        Assert.Equal(-1150, motor.SpeedDelta);
        Assert.Equal(MotorStatus.Stopping, motor.Status);
    }

    [Fact]
    public void WhenPoweredOffMotorReachesZero_ThenItIsStopped()
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = 1200;
        motor.CurrentSpeed = 50;
        motor.IsPowered = false;

        // Act
        motor.SimulateStep();

        // Assert
        Assert.Equal(0, motor.CurrentSpeed);
        Assert.Equal(MotorStatus.Stopped, motor.Status);
    }

    [Fact]
    public void WhenPoweredOnAgain_ThenItStartsAndThenRuns()
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = 1200;
        motor.IsPowered = false;
        motor.Status = MotorStatus.Stopped;

        // Act
        motor.PowerOn();
        motor.SimulateStep();
        var statusAfterFirstStep = motor.Status;
        motor.SimulateStep();

        // Assert
        Assert.Equal(MotorStatus.Starting, statusAfterFirstStep);
        Assert.Equal(MotorStatus.Running, motor.Status);
        Assert.Equal(100, motor.CurrentSpeed);
    }

    [Theory]
    [InlineData(MotorMode.Eco, 1000, 600)]
    [InlineData(MotorMode.Normal, 1000, 1000)]
    [InlineData(MotorMode.Boost, 1000, 1200)]
    [InlineData(MotorMode.Boost, 3000, 3000)]
    [InlineData(MotorMode.Eco, 1200, 720)]
    public void WhenModeIsSet_ThenItScalesTheSimulationTargetWithinTheRange(MotorMode mode, int targetSpeed, int expectedTarget)
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = targetSpeed;
        motor.Mode = mode;

        // Act
        var simulationTarget = motor.GetSimulationTarget();

        // Assert
        Assert.Equal(expectedTarget, simulationTarget);
        Assert.Equal(targetSpeed, motor.TargetSpeed);
    }

    [Fact]
    public void WhenSpeedReachedTheTargetScaledByTheMode_ThenItIsAtTargetSpeed()
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = 1000;

        // Act
        motor.Mode = MotorMode.Eco;
        motor.CurrentSpeed = 600;

        // Assert
        Assert.Equal(0, motor.SpeedDelta);
        Assert.True(motor.IsAtTargetSpeed);
    }

    [Fact]
    public void WhenNotPowered_ThenTheSpeedIsMeasuredAgainstZero()
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = 1000;
        motor.CurrentSpeed = 1000;

        // Act
        motor.PowerOff();

        // Assert
        Assert.Equal(-1000, motor.SpeedDelta);
        Assert.False(motor.IsAtTargetSpeed);
    }

    [Fact]
    public void WhenNotPowered_ThenSimulationTargetIsZero()
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = 1000;
        motor.IsPowered = false;

        // Act
        var simulationTarget = motor.GetSimulationTarget();

        // Assert
        Assert.Equal(0, simulationTarget);
    }

    [Fact]
    public void WhenEmergencyStopped_ThenItIsPoweredOffAtZeroSpeedAndKeepsTargetSpeed()
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = 1200;
        motor.CurrentSpeed = 1200;
        motor.Status = MotorStatus.Running;

        // Act
        motor.EmergencyStop();

        // Assert
        Assert.False(motor.IsPowered);
        Assert.Equal(0, motor.CurrentSpeed);
        Assert.Equal(1200, motor.TargetSpeed);
        Assert.Equal(MotorStatus.Stopped, motor.Status);
    }

    [Fact]
    public void WhenPoweredOnAfterEmergencyStop_ThenItSpeedsUpTowardsItsTargetAgain()
    {
        // Arrange
        var motor = CreateMotor();
        motor.TargetSpeed = 1200;
        motor.CurrentSpeed = 1200;
        motor.EmergencyStop();

        // Act
        motor.PowerOn();
        motor.SimulateStep();

        // Assert
        Assert.True(motor.IsPowered);
        Assert.Equal(50, motor.CurrentSpeed);
        Assert.Equal(MotorStatus.Starting, motor.Status);
    }

    [Fact]
    public void WhenPowerChanges_ThenOnlyTheOperationForTheOtherStateIsEnabled()
    {
        // Arrange
        var motor = CreateMotor();
        var registeredMotor = motor.TryGetRegisteredSubject()!;
        var powerOn = registeredMotor.TryGetProperty(nameof(Motor.PowerOn))!;
        var powerOff = registeredMotor.TryGetProperty(nameof(Motor.PowerOff))!;

        // Act
        var whilePowered = (PowerOn: powerOn.IsMethodEnabled(), PowerOff: powerOff.IsMethodEnabled());
        motor.PowerOff();
        var whileNotPowered = (PowerOn: powerOn.IsMethodEnabled(), PowerOff: powerOff.IsMethodEnabled());

        // Assert
        Assert.Equal((false, true), whilePowered);
        Assert.Equal((true, false), whileNotPowered);
    }

    [Fact]
    public void WhenLoadIsFull_ThenTemperatureIsTenDegreesHigher()
    {
        // Arrange
        var motor = CreateMotor();
        motor.Load = 0;
        motor.SimulateStep();
        var temperatureWithoutLoad = motor.Temperature!.Value;

        // Act
        motor.Load = 1;
        motor.SimulateStep();

        // Assert
        Assert.InRange(temperatureWithoutLoad, 24m, 26m);
        Assert.InRange(motor.Temperature!.Value, 34m, 36m);
    }
}
