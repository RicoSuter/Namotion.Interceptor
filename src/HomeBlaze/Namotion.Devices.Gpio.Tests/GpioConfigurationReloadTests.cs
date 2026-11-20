using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Devices.Gpio.Simulation;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// Applies configurations to a running subject, which stops the GPIO hardware handling and connects again: as the
/// storage applies a changed file (the serializer replaces the configuration properties, which deserializes new pins,
/// and then the configuration is applied) and as the pin editor saves a pin (the same pins, then applied).
/// </summary>
public class GpioConfigurationReloadTests
{
    private const string ReloadedJson = """
        {
          "$type": "Namotion.Devices.Gpio.GpioSubject",
          "pins": {
            "5": { "mode": "Output" },
            "17": { "name": "Led", "mode": "Output" },
            "18": { "mode": "InputPullDown", "debounceTime": "00:00:00" },
            "27": { "name": "Relay", "mode": "InputPullUp" }
          }
        }
        """;

    [Fact]
    public async Task WhenConfigurationIsReloaded_ThenPinsRunWithTheSettingsOfTheFile()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Output, inputLevel: PinValue.Low, latch: PinValue.High);
        var subject = CreateSubject(driver);
        var serializer = GpioTestSubject.CreateSerializer(subject);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        var pinBeforeReload = subject.Pins[17];

        // Act
        serializer.UpdateConfiguration(subject, ReloadedJson);
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(28, subject.Pins.Count);
        Assert.All(subject.Pins, entry =>
        {
            Assert.Equal(entry.Key, entry.Value.PinNumber);
            Assert.Equal(ServiceStatus.Running, entry.Value.Status);
        });

        var led = subject.Pins[17];
        Assert.Equal(GpioPinMode.Output, led.Mode);
        Assert.Equal(PinMode.Output, driver.GetMode(17));
        Assert.Equal("Led (Pin 17)", led.Title);
        Assert.Equal("Relay", subject.Pins[27].Name);
        Assert.Equal(PinMode.InputPullUp, driver.GetMode(27));
        Assert.Equal(ServiceStatus.Stopped, pinBeforeReload.Status);

        // Pin 6 was an output and is missing from the file, so it starts like a new pin.
        Assert.Equal(GpioPinMode.Input, subject.Pins[6].Mode);
        Assert.Equal(PinMode.Input, driver.GetMode(6));

        Assert.True(subject.Pins[5].Value);
        Assert.Equal(PinValue.High, driver.GetLatch(5));
        Assert.DoesNotContain(PinValue.Low, driver.GetDrivenLevels(5));

        await led.TurnOnAsync(CancellationToken.None);
        Assert.Equal(PinValue.High, driver.GetLatch(17));
    }

    [Fact]
    public async Task WhenConfigurationIsReloaded_ThenEdgesOfAnInputReachTheReloadedPin()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        var subject = CreateSubject(driver);
        var serializer = GpioTestSubject.CreateSerializer(subject);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        serializer.UpdateConfiguration(subject, ReloadedJson);
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        driver.SimulatePinValueChange(18, PinValue.High);

        // Assert
        Assert.True(subject.Pins[18].Value);
    }

    [Fact]
    public async Task WhenPinIsLeftOutOfReloadedConfigurationOnRaspberryPi_ThenItStartsWithItsPowerOnPull()
    {
        // Arrange
        const string jsonWithoutPin5 = """
            {
              "$type": "Namotion.Devices.Gpio.GpioSubject",
              "pins": {
                "17": { "name": "Led", "mode": "Output" }
              }
            }
            """;
        var driver = new LatchingGpioDriver();
        var subject = CreateSubject(driver, isRaspberryPi: true);
        var serializer = GpioTestSubject.CreateSerializer(subject);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        Assert.Equal(PinMode.Output, driver.GetMode(5));

        // Act
        serializer.UpdateConfiguration(subject, jsonWithoutPin5);
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[5].Mode);
        Assert.Equal(PinMode.InputPullUp, driver.GetMode(5));
        Assert.Equal(GpioPinMode.InputPullDown, subject.Pins[27].Mode);
        Assert.Equal(PinMode.InputPullDown, driver.GetMode(27));
        Assert.Equal(ServiceStatus.Running, subject.Pins[5].Status);
    }

    [Fact]
    public async Task WhenDriverCannotReadBeforeModeIsSet_ThenAReloadedOutputKeepsItsLevel()
    {
        // Arrange
        var driver = new LatchingGpioDriver { FailsReadBeforeMode = true };
        var subject = CreateSubject(driver);
        var serializer = GpioTestSubject.CreateSerializer(subject);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        await subject.Pins[5].TurnOnAsync(CancellationToken.None);
        var drivenLevelCountBeforeReload = driver.GetDrivenLevels(5).Length;

        // Act
        serializer.UpdateConfiguration(subject, ReloadedJson);
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, subject.Pins[5].Status);
        Assert.True(subject.Pins[5].Value);
        Assert.Equal(PinValue.High, driver.GetLatch(5));
        Assert.DoesNotContain(PinValue.Low, driver.GetDrivenLevels(5).Skip(drivenLevelCountBeforeReload));
    }

    [Fact]
    public async Task WhenModeOfAPinIsChangedAndTheConfigurationApplied_ThenItIsAnOutputAndTheOtherPinsAreUnchanged()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = CreateSubject(driver);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        var pinsBefore = subject.Pins.ToDictionary(entry => entry.Key, entry => (entry.Value, entry.Value.Mode));
        var pin = subject.Pins[17];

        // Act
        pin.Mode = GpioPinMode.Output;
        await pin.TurnOnAsync(CancellationToken.None);
        var drivenLevelCountBeforeApply = driver.GetDrivenLevels(17).Length;
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(pin, subject.Pins[17]);
        Assert.Equal(ServiceStatus.Running, pin.Status);
        Assert.Equal(GpioPinMode.Output, pin.Mode);
        Assert.Equal(PinMode.Output, driver.GetMode(17));
        Assert.True(pin.Value);
        Assert.Equal(PinValue.High, driver.GetLatch(17));
        Assert.DoesNotContain(PinValue.Low, driver.GetDrivenLevels(17).Skip(drivenLevelCountBeforeApply));

        Assert.All(pinsBefore.Where(entry => entry.Key != 17), entry =>
        {
            Assert.Same(entry.Value.Value, subject.Pins[entry.Key]);
            Assert.Equal(entry.Value.Mode, subject.Pins[entry.Key].Mode);
            Assert.Equal(ServiceStatus.Running, subject.Pins[entry.Key].Status);
        });
    }

    [Fact]
    public async Task WhenAPinCannotBeReleased_ThenTheRestartCompletesAndReportsIt()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var subject = GpioTestSubject.Create(driver, adcFactory: adcFactory, pins: new Dictionary<int, GpioPin>
        {
            [18] = new() { Mode = GpioPinMode.InputPullDown }
        });
        subject.Mcp3008 = GpioTestSubject.CreateMcp3008Configuration();
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject, GpioTestSubject.Mcp3008PinNumbers);
        driver.FailingCallbackRemovalPinNumber = 18;

        // Act
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, subject.Status);
        Assert.Equal("GPIO pin 18 could not be released: The callback of pin 18 could not be removed.", subject.StatusMessage);
        Assert.Equal(["create MCP3008 21", "dispose MCP3008 21", "create MCP3008 21"], adcFactory.Events);
        Assert.All(subject.Pins.Where(entry => !GpioTestSubject.Mcp3008PinNumbers.Contains(entry.Key)),
            entry => Assert.Equal(ServiceStatus.Running, entry.Value.Status));
    }

    [Fact]
    public async Task WhenConfigurationIsReloadedWithoutController_ThenPinsHaveTheirNumbers()
    {
        // Arrange
        var subject = CreateSubject(new LatchingGpioDriver { IsUnavailable = true });
        var serializer = GpioTestSubject.CreateSerializer(subject);
        try
        {
            // Act
            serializer.UpdateConfiguration(subject, ReloadedJson);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal(ServiceStatus.Error, subject.Status);
            Assert.Equal("Failed to initialize GPIO: No GPIO chip found.", subject.StatusMessage);
            Assert.Equal([5, 17, 18, 27], subject.Pins.Keys.Order());
            Assert.All(subject.Pins, entry =>
            {
                Assert.Equal(entry.Key, entry.Value.PinNumber);
                Assert.Equal(ServiceStatus.Stopped, entry.Value.Status);
            });
        }
        finally
        {
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenConfigurationIsAppliedAfterDispose_ThenNothingIsCreated()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateSubject(driver, adcFactory: adcFactory);
        subject.Mcp3008 = GpioTestSubject.CreateMcp3008Configuration();
        subject.Dispose();

        // Act
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.Empty(adcFactory.Events);
        Assert.Equal(0, driver.GetOpenCount(17));
        Assert.Equal(ServiceStatus.Stopped, subject.Pins[17].Status);
    }

    [Fact]
    public async Task WhenSubjectIsDisposedWithoutStop_ThenItStaysStopped()
    {
        // Arrange
        var subject = CreateSubject(new LatchingGpioDriver());
        await subject.StartAsync(CancellationToken.None);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Act
        subject.Dispose();
        await subject.ExecuteTask!.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.Null(subject.StatusMessage);
        Assert.All(subject.Pins.Values, pin => Assert.Equal(ServiceStatus.Stopped, pin.Status));
    }

    private static GpioSubject CreateSubject(GpioDriver driver, bool isRaspberryPi = false, RecordingAdcFactory? adcFactory = null) =>
        GpioTestSubject.Create(driver, isRaspberryPi, adcFactory, new Dictionary<int, GpioPin>
        {
            [5] = new() { Mode = GpioPinMode.Output },
            [6] = new() { Mode = GpioPinMode.Output },
            [17] = new() { Mode = GpioPinMode.InputPullDown },
            [27] = new() { Mode = GpioPinMode.InputPullDown }
        });
}
