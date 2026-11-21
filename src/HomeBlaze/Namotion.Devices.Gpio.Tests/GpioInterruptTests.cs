using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Simulation;
using Namotion.Interceptor.Testing;
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

    [Fact]
    public async Task WhenAnEdgeArrivesWhileItsInputBecomesAnOutput_ThenNeitherWaitsForTheOther()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver, pins: new Dictionary<int, GpioPin>
        {
            [17] = new() { Mode = GpioPinMode.Input, DebounceTime = TimeSpan.Zero }
        });
        await subject.StartAsync(CancellationToken.None);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        var pin = subject.Pins[17];

        using var edgeDispatching = new ManualResetEventSlim();
        Task? edgeTask = null;
        driver.CallbackRemoving = pinNumber =>
        {
            // The mode change holds the pin lock here; the edge holds the callback lock while it calls the callback.
            driver.CallbackRemoving = null;
            edgeTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
                () => driver.RaiseEdge(pinNumber, PinValue.High, edgeDispatching.Set));
            edgeDispatching.Wait(TestTimeouts.Default);
        };

        // Act
        await DedicatedThreadTestHelpers
            .RunOnDedicatedThreadAsync(() => pin.Mode = GpioPinMode.Output)
            .WaitAsync(TestTimeouts.Default);
        await edgeTask!.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(GpioPinMode.Output, pin.Mode);
        Assert.Equal(ServiceStatus.Running, pin.Status);
        Assert.Equal(PinMode.Output, driver.GetMode(17));

        // Not disposed when an assertion fails: with the deadlock, the stop would wait for the driver lock forever.
        await subject.StopAsync(CancellationToken.None);
        subject.Dispose();
    }

    [Fact]
    public async Task WhenAnEdgeFindsItsPinBusyAndTheLaterReadFails_ThenThePinReportsTheError()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver, pins: new Dictionary<int, GpioPin>
        {
            [17] = new() { Mode = GpioPinMode.Input, DebounceTime = TimeSpan.Zero }
        });
        await subject.StartAsync(CancellationToken.None);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        var pin = subject.Pins[17];

        Task? edgeTask = null;
        driver.CallbackRemoving = pinNumber =>
        {
            // The mode change holds the pin lock here, so the edge queues a read, which runs once the change is done.
            driver.CallbackRemoving = null;
            driver.FailsReads = true;
            edgeTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => driver.RaiseEdge(pinNumber, PinValue.High));
            edgeTask.Wait(TestTimeouts.Default);
        };

        // Act
        await DedicatedThreadTestHelpers
            .RunOnDedicatedThreadAsync(() => pin.Mode = GpioPinMode.Output)
            .WaitAsync(TestTimeouts.Default);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => pin.Status == ServiceStatus.Error);
        Assert.Contains("after an edge", pin.StatusMessage);

        driver.FailsReads = false;
        await subject.StopAsync(CancellationToken.None);
        subject.Dispose();
    }

    [Fact]
    public async Task WhenAnEdgeArrivesWhileAnotherInputBecomesAnOutput_ThenNeitherWaitsForTheOther()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver, pins: new Dictionary<int, GpioPin>
        {
            [17] = new() { Mode = GpioPinMode.Input, DebounceTime = TimeSpan.Zero },
            [22] = new() { Mode = GpioPinMode.Input, DebounceTime = TimeSpan.Zero }
        });
        await subject.StartAsync(CancellationToken.None);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        using var edgeDispatching = new ManualResetEventSlim();
        Task? edgeTask = null;
        driver.CallbackRemoving = pinNumber =>
        {
            // The mode change of pin 22 holds the driver lock here; the edge of pin 17 holds the callback lock while it
            // calls the callback, which must not call the driver.
            driver.CallbackRemoving = null;
            edgeTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
                () => driver.RaiseEdge(17, PinValue.High, edgeDispatching.Set));
            edgeDispatching.Wait(TestTimeouts.Default);
        };

        // Act
        await DedicatedThreadTestHelpers
            .RunOnDedicatedThreadAsync(() => subject.Pins[22].Mode = GpioPinMode.Output)
            .WaitAsync(TestTimeouts.Default);
        await edgeTask!.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(ServiceStatus.Running, subject.Pins[17].Status);
        Assert.Equal(PinMode.Output, driver.GetMode(22));

        // Not disposed when an assertion fails: with the deadlock, the stop would wait for the driver lock forever.
        await subject.StopAsync(CancellationToken.None);
        subject.Dispose();
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
        using var controller = new GpioController(simulationDriver);
        controller.OpenPin(17, PinMode.Output);

        var pin = new GpioPin
        {
            Controller = controller,
            PinNumber = 17,
            Mode = GpioPinMode.Output,
        };
        pin.Attach(controller, _ => { }, _ => { });

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
        using var controller = new GpioController(simulationDriver);
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
        using var controller = new GpioController(simulationDriver);

        var pins = new List<GpioPin>();
        for (int i = 17; i <= 20; i++)
        {
            controller.OpenPin(i, PinMode.Output);
            var pin = new GpioPin
            {
                Controller = controller,
                PinNumber = i,
                Mode = GpioPinMode.Output,
            };
            pin.Attach(controller, _ => { }, _ => { });
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
