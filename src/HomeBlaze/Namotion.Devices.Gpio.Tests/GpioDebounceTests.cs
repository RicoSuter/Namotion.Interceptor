using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Gpio.Simulation;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioDebounceTests
{
    private static readonly TimeSpan DebounceTime = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void WhenPinIsCreated_ThenDebounceTimeIs50Milliseconds()
    {
        // Act
        var pin = new GpioPin();

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(50), pin.DebounceTime);
    }

    [Fact]
    public void WhenDebounceTimeIsNegative_ThenSettingItThrows()
    {
        // Arrange
        var pin = new GpioPin();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => pin.DebounceTime = TimeSpan.FromMilliseconds(-1));
    }

    [Fact]
    public void WhenInputChanges_ThenValueChangesAtOnce()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);

        // Act
        driver.SimulatePinValueChange(17, PinValue.High);
        pin.ApplyHardwareLevel(isHigh: true);

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public void WhenEdgesBounceWithinDebounceTime_ThenValueChangesOnce()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);
        var changes = new List<bool>();
        pin.PropertyChanged += (_, arguments) =>
        {
            if (arguments.PropertyName == nameof(GpioPin.Value))
            {
                changes.Add(pin.Value);
            }
        };

        // Act
        foreach (var isHigh in new[] { true, false, true, false, true })
        {
            driver.SimulatePinValueChange(17, isHigh ? PinValue.High : PinValue.Low);
            pin.ApplyHardwareLevel(isHigh);
            timeProvider.Advance(TimeSpan.FromMilliseconds(5));
        }

        timeProvider.Advance(DebounceTime);

        // Assert
        Assert.Equal([true], changes);
        Assert.True(pin.Value);
    }

    [Fact]
    public void WhenLevelSettlesDifferentlyWithinDebounceTime_ThenValueIsCorrectedWhenItElapses()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);

        driver.SimulatePinValueChange(17, PinValue.High);
        pin.ApplyHardwareLevel(isHigh: true);
        timeProvider.Advance(TimeSpan.FromMilliseconds(10));
        driver.SimulatePinValueChange(17, PinValue.Low);
        pin.ApplyHardwareLevel(isHigh: false);
        Assert.True(pin.Value);

        // Act
        timeProvider.Advance(DebounceTime);

        // Assert
        Assert.False(pin.Value);
    }

    [Fact]
    public void WhenDebounceTimeElapsed_ThenTheNextEdgeChangesValueAtOnce()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);

        driver.SimulatePinValueChange(17, PinValue.High);
        pin.ApplyHardwareLevel(isHigh: true);
        timeProvider.Advance(DebounceTime);

        // Act
        driver.SimulatePinValueChange(17, PinValue.Low);
        pin.ApplyHardwareLevel(isHigh: false);

        // Assert
        Assert.False(pin.Value);
    }

    [Fact]
    public void WhenDebounceTimeIsZero_ThenEveryEdgeChangesValue()
    {
        // Arrange
        var (_, controller, timeProvider) = CreateController(PinMode.Input);
        using var __ = controller;
        var pin = CreateRunningInput(controller, timeProvider);
        pin.DebounceTime = TimeSpan.Zero;

        // Act & Assert
        pin.ApplyHardwareLevel(isHigh: true);
        Assert.True(pin.Value);
        pin.ApplyHardwareLevel(isHigh: false);
        Assert.False(pin.Value);
        pin.ApplyHardwareLevel(isHigh: true);
        Assert.True(pin.Value);
    }

    [Fact]
    public void WhenPinIsOutput_ThenLevelChangesAreNotDebounced()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Output);
        using var __ = controller;
        var pin = new GpioPin
        {
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            TimeProvider = timeProvider
        };
        pin.Attach(controller, _ => { }, _ => { });

        // Act & Assert
        driver.SimulatePinValueChange(17, PinValue.High);
        pin.PollHardwareLevel();
        Assert.True(pin.Value);
        driver.SimulatePinValueChange(17, PinValue.Low);
        pin.PollHardwareLevel();
        Assert.False(pin.Value);
    }

    [Fact]
    public void WhenInputChangesToOutput_ThenALateEdgeDoesNotWriteTheOutput()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);
        pin.RegisterInterrupt = _ => { };
        pin.UnregisterInterrupt = _ => { };
        pin.Mode = GpioPinMode.Output;

        // Act
        pin.ApplyHardwareLevel(isHigh: true);

        // Assert
        Assert.False(pin.Value);
        Assert.Equal(PinValue.Low, driver.PinValues[17]);
    }

    [Fact]
    public void WhenPinChangesToOutputWithinDebounceTime_ThenTheElapsedWindowChangesNothing()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);
        pin.RegisterInterrupt = _ => { };
        pin.UnregisterInterrupt = _ => { };

        driver.SimulatePinValueChange(17, PinValue.High);
        pin.ApplyHardwareLevel(isHigh: true);
        pin.Mode = GpioPinMode.Output;
        pin.Value = false;

        // Act
        timeProvider.Advance(DebounceTime);

        // Assert
        Assert.False(pin.Value);
        Assert.Equal(PinValue.Low, driver.PinValues[17]);
    }

    [Fact]
    public void WhenDebounceTimeIsAboveMaximum_ThenSettingItThrows()
    {
        // Arrange
        var pin = new GpioPin();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => pin.DebounceTime = GpioPin.MaximumDebounceTime + TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void WhenDebounceTimeIsMaximum_ThenSettingItIsAccepted()
    {
        // Arrange
        var pin = new GpioPin();

        // Act
        pin.DebounceTime = GpioPin.MaximumDebounceTime;

        // Assert
        Assert.Equal(GpioPin.MaximumDebounceTime, pin.DebounceTime);
    }

    [Fact]
    public void WhenHardwareLevelChangedWithoutInterrupt_ThenPollAppliesIt()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);
        driver.SimulatePinValueChange(17, PinValue.High);

        // Act
        pin.PollHardwareLevel();

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public void WhenPinIsNotRunning_ThenPollChangesNothing()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);
        pin.Status = ServiceStatus.Unavailable;
        driver.SimulatePinValueChange(17, PinValue.High);

        // Act
        pin.PollHardwareLevel();

        // Assert
        Assert.False(pin.Value);
    }

    [Fact]
    public void WhenDebounceWindowIsOpen_ThenPollChangesNothing()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);
        driver.SimulatePinValueChange(17, PinValue.High);
        pin.ApplyHardwareLevel(isHigh: true);
        driver.SimulatePinValueChange(17, PinValue.Low);

        // Act
        pin.PollHardwareLevel();

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public void WhenOutputLevelChangedExternally_ThenPollAppliesIt()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Output);
        using var _ = controller;
        var pin = new GpioPin
        {
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            TimeProvider = timeProvider
        };
        pin.Attach(controller, _ => { }, _ => { });
        driver.SimulatePinValueChange(17, PinValue.High);

        // Act
        pin.PollHardwareLevel();

        // Assert
        Assert.True(pin.Value);
    }

    [Fact]
    public void WhenReadFailsWhenDebounceWindowEnds_ThenStatusIsErrorAndNothingIsThrown()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        var pin = CreateRunningInput(controller, timeProvider);
        driver.SimulatePinValueChange(17, PinValue.High);
        pin.ApplyHardwareLevel(isHigh: true);
        controller.Dispose();

        // Act
        timeProvider.Advance(DebounceTime);

        // Assert
        Assert.Equal(ServiceStatus.Error, pin.Status);
        Assert.NotNull(pin.StatusMessage);
    }

    [Fact]
    public void WhenInversionChangesWithinDebounceTime_ThenTheWindowIsClosedAndTheNextEdgeIsApplied()
    {
        // Arrange
        var (driver, controller, timeProvider) = CreateController(PinMode.Input);
        using var _ = controller;
        var pin = CreateRunningInput(controller, timeProvider);
        driver.SimulatePinValueChange(17, PinValue.High);
        pin.ApplyHardwareLevel(isHigh: true);
        pin.IsInverted = true;
        Assert.False(pin.Value);

        // Act
        driver.SimulatePinValueChange(17, PinValue.Low);
        pin.ApplyHardwareLevel(isHigh: false);

        // Assert
        Assert.True(pin.Value);
    }

    private static (SimulationGpioDriver Driver, GpioController Controller, FakeTimeProvider TimeProvider) CreateController(PinMode mode)
    {
        var driver = new SimulationGpioDriver();
        var controller = new GpioController(driver);
        controller.OpenPin(17, mode);
        return (driver, controller, new FakeTimeProvider());
    }

    private static GpioPin CreateRunningInput(GpioController controller, TimeProvider timeProvider)
    {
        var pin = new GpioPin
        {
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            DebounceTime = DebounceTime,
            Status = ServiceStatus.Running,
            TimeProvider = timeProvider
        };

        pin.Controller = controller;
        return pin;
    }
}
