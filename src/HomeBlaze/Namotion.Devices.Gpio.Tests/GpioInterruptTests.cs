using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Simulation;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// Integration tests for GPIO interrupt handling and value synchronization.
/// </summary>
public class GpioInterruptTests
{
    [Fact]
    public async Task WhenInputOfStartedSubjectGoesHighAndLow_ThenValueFollows()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        var subject = CreateSubjectWithInput(driver);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Act & Assert
        driver.SimulatePinValueChange(18, PinValue.High);
        Assert.True(subject.Pins[18].Value);
        driver.SimulatePinValueChange(18, PinValue.Low);
        Assert.False(subject.Pins[18].Value);
    }

    [Fact]
    public async Task WhenInputChangesAfterSubjectIsDisposed_ThenValueIsKept()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        var subject = CreateSubjectWithInput(driver);
        var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        await running.DisposeAsync();

        // Act
        driver.SimulatePinValueChange(18, PinValue.High);

        // Assert
        Assert.False(subject.Pins[18].Value);
    }

    [Fact]
    public async Task WhenSubjectIsDisposed_ThenPinsAreStoppedAndIgnoreLevels()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        var subject = CreateSubjectWithInput(driver);
        await subject.StartAsync(CancellationToken.None);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        await subject.StopAsync(CancellationToken.None);
        var pin = subject.Pins[18];

        // Act
        subject.Dispose();
        pin.ApplyHardwareLevel(isHigh: true);
        pin.PollHardwareLevel();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, pin.Status);
        Assert.Null(pin.StatusMessage);
        Assert.False(pin.Value);
    }

    [Fact]
    public void WhenPinIsNotRunning_ThenHardwareLevelIsIgnored()
    {
        // Arrange
        var pin = new GpioPin { PinNumber = 18, DebounceTime = TimeSpan.Zero, Status = ServiceStatus.Unavailable };

        // Act
        pin.ApplyHardwareLevel(isHigh: true);

        // Assert
        Assert.False(pin.Value);
    }

    private static GpioSubject CreateSubjectWithInput(SimulationGpioDriver driver)
    {
        return GpioTestSubject.Create(driver, pins: new Dictionary<int, GpioPin>
        {
            [18] = new() { Mode = GpioPinMode.Input, DebounceTime = TimeSpan.Zero }
        });
    }

    [Fact]
    public void FullWorkflow_OutputWrite_ReadBack_Verify()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Output);

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Output,
            Status = ServiceStatus.Running
        };

        // Act - Write high
        pin.Value = true;

        // Assert
        Assert.Equal(PinValue.High, simulationDriver.PinValues[17]);
        Assert.Equal(ServiceStatus.Running, pin.Status); // No error

        // Act - Write low
        pin.Value = false;

        // Assert
        Assert.Equal(PinValue.Low, simulationDriver.PinValues[17]);
        Assert.Equal(ServiceStatus.Running, pin.Status);
    }

    [Fact]
    public void FullWorkflow_ModeChange_InterruptManagement()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);
        controller.OpenPin(17, PinMode.Input);

        var registeredInterrupts = new HashSet<int>();

        var pin = new GpioPin
        {
            Controller = controller,
            RegisterInterrupt = pin => registeredInterrupts.Add(pin),
            UnregisterInterrupt = pin => registeredInterrupts.Remove(pin),
            PinNumber = 17,
            Mode = GpioPinMode.Input,
            Status = ServiceStatus.Running
        };

        // Register interrupt initially
        registeredInterrupts.Add(17);

        // Act - Change to output
        pin.Mode = GpioPinMode.Output;

        // Assert - Interrupt should be unregistered
        Assert.DoesNotContain(17, registeredInterrupts);
        Assert.Equal(PinMode.Output, simulationDriver.PinModes[17]);

        // Act - Change back to input
        pin.Mode = GpioPinMode.Input;

        // Assert - Interrupt should be registered again
        Assert.Contains(17, registeredInterrupts);
        Assert.Equal(PinMode.Input, simulationDriver.PinModes[17]);
    }

    [Fact]
    public void MultiplePins_IndependentOperation()
    {
        // Arrange
        var simulationDriver = new SimulationGpioDriver();
        using var controller = new GpioController(PinNumberingScheme.Logical, simulationDriver);

        var pins = new List<GpioPin>();
        for (int i = 17; i <= 20; i++)
        {
            controller.OpenPin(i, PinMode.Output);
            var pin = new GpioPin
            {
                Controller = controller,
                PinNumber = i,
                Mode = GpioPinMode.Output,
                Status = ServiceStatus.Running
            };
            pins.Add(pin);
        }

        // Act - Set different values
        pins[0].Value = true;  // Pin 17
        pins[1].Value = false; // Pin 18
        pins[2].Value = true;  // Pin 19
        pins[3].Value = false; // Pin 20

        // Assert - Each pin has independent value
        Assert.Equal(PinValue.High, simulationDriver.PinValues[17]);
        Assert.Equal(PinValue.Low, simulationDriver.PinValues[18]);
        Assert.Equal(PinValue.High, simulationDriver.PinValues[19]);
        Assert.Equal(PinValue.Low, simulationDriver.PinValues[20]);
    }
}
