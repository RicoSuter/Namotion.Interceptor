using System.Device.Gpio;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Devices.Gpio.Simulation;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// Reloads the configuration of a running subject the way the storage applies a changed file: the serializer replaces
/// the configuration properties, which deserializes new pins, and then the configuration is applied.
/// </summary>
public class GpioConfigurationReloadTests
{
    private const string ReloadedJson = """
        {
          "$type": "Namotion.Devices.Gpio.GpioSubject",
          "pins": {
            "5": { "mode": "Output" },
            "6": { "mode": "InputPullUp" },
            "17": { "name": "Led", "mode": "Output" },
            "18": { "mode": "InputPullDown", "debounceTime": "00:00:00" },
            "27": { "name": "Relay", "mode": "Output" }
          }
        }
        """;

    private const string JsonWithMcp3008 = """
        {
          "$type": "Namotion.Devices.Gpio.GpioSubject",
          "mcp3008": { "clockPin": 21, "mosiPin": 20, "misoPin": 19, "chipSelectPin": 16 },
          "pins": {
            "17": { "name": "Led", "mode": "Output" }
          }
        }
        """;

    private const string JsonWithoutMcp3008 = """
        {
          "$type": "Namotion.Devices.Gpio.GpioSubject",
          "mcp3008": null,
          "pins": {
            "17": { "name": "Led", "mode": "Output" }
          }
        }
        """;

    private const string JsonWithMovedMcp3008ClockPin = """
        {
          "$type": "Namotion.Devices.Gpio.GpioSubject",
          "mcp3008": { "clockPin": 26, "mosiPin": 20, "misoPin": 19, "chipSelectPin": 16 },
          "pins": {
            "17": { "name": "Led", "mode": "Output" }
          }
        }
        """;

    private static readonly int[] Mcp3008PinNumbers = [16, 19, 20, 21];

    [Fact]
    public async Task WhenConfigurationIsReloaded_ThenPinsKeepTheirNumbersAndApplyTheFile()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Output, inputLevel: PinValue.Low, latch: PinValue.High);
        var (subject, serializer) = CreateSubject(driver);
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject);
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
            Assert.True(led.TurnOn_IsEnabled);
            Assert.Equal("Relay", subject.Pins[27].Name);
            Assert.Equal(PinMode.InputPullUp, driver.GetMode(6));

            Assert.True(subject.Pins[5].Value);
            Assert.Equal(PinValue.High, driver.GetLatch(5));
            Assert.DoesNotContain(PinValue.Low, driver.GetDrivenLevels(5));

            Assert.Equal(ServiceStatus.Stopped, pinBeforeReload.Status);

            await led.TurnOnAsync(CancellationToken.None);
            Assert.Equal(PinValue.High, driver.GetLatch(17));
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenConfigurationIsReloaded_ThenEdgesOfAnInputReachTheReloadedPin()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        var (subject, serializer) = CreateSubject(driver);
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject);
            serializer.UpdateConfiguration(subject, ReloadedJson);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Act
            driver.SimulatePinValueChange(18, PinValue.High);

            // Assert
            Assert.True(subject.Pins[18].Value);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenOutputPinIsLeftOutOfReloadedConfiguration_ThenPinBecomesInput()
    {
        // Arrange
        const string jsonWithoutPin5 = """
            {
              "$type": "Namotion.Devices.Gpio.GpioSubject",
              "pins": {
                "6": { "mode": "InputPullUp" },
                "17": { "name": "Led", "mode": "Output" },
                "27": { "name": "Relay", "mode": "Output" }
              }
            }
            """;
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Output, inputLevel: PinValue.Low, latch: PinValue.High);
        var (subject, serializer) = CreateSubject(driver);
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject);

            // Act
            serializer.UpdateConfiguration(subject, jsonWithoutPin5);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal(28, subject.Pins.Count);
            Assert.All(subject.Pins, entry =>
            {
                Assert.Equal(entry.Key, entry.Value.PinNumber);
                Assert.Equal(ServiceStatus.Running, entry.Value.Status);
            });

            var pin5 = subject.Pins[5];
            Assert.Equal(GpioPinMode.Input, pin5.Mode);
            Assert.Equal(PinMode.Input, driver.GetMode(5));

            var led = subject.Pins[17];
            Assert.Equal(GpioPinMode.Output, led.Mode);
            Assert.Equal(PinMode.Output, driver.GetMode(17));
            Assert.Equal("Led (Pin 17)", led.Title);
            Assert.Equal("Relay", subject.Pins[27].Name);
            Assert.Equal(PinMode.InputPullUp, driver.GetMode(6));
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenConfigurationIsReloadedWithoutController_ThenPinsKeepTheirNumbers()
    {
        // Arrange
        var (subject, serializer) = CreateSubject(new LatchingGpioDriver());
        try
        {
            // Act
            serializer.UpdateConfiguration(subject, ReloadedJson);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal([5, 6, 17, 18, 27], subject.Pins.Keys.Order());
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
    public async Task WhenConfigurationIsAppliedAfterDispose_ThenNoControllerIsCreated()
    {
        // Arrange
        var subject = new GpioSubject();
        subject.Dispose();
        subject.UseSimulation = true;

        // Act
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Empty(subject.Pins);
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
    }

    [Fact]
    public async Task WhenUnchangedConfigurationIsAppliedAgain_ThenPinsAreKept()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var (subject, _) = CreateSubject(driver);
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject);
            var pinsBefore = subject.Pins.Values.ToArray();

            // Act
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal(pinsBefore, subject.Pins.Values);
            Assert.All(subject.Pins.Values, pin => Assert.Equal(ServiceStatus.Running, pin.Status));
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
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
        var (subject, serializer) = CreateSubject(driver, isRaspberryPi: true);
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject);
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
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenConfigurationWithMcp3008IsReloaded_ThenItsPinsAreNeverOpened()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var (subject, serializer) = CreateSubject(driver, adcFactory: adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject, Mcp3008PinNumbers);

            // Act
            serializer.UpdateConfiguration(subject, JsonWithMcp3008);
            await subject.ApplyConfigurationAsync(CancellationToken.None);
            serializer.UpdateConfiguration(subject, JsonWithMcp3008);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            foreach (var pinNumber in Mcp3008PinNumbers)
            {
                var pin = subject.Pins[pinNumber];
                Assert.Equal(pinNumber, pin.PinNumber);
                Assert.Equal(ServiceStatus.Unavailable, pin.Status);
                Assert.Equal("Used by the MCP3008", pin.StatusMessage);
                Assert.Null(pin.Controller);
                Assert.Equal(0, driver.GetOpenCount(pinNumber));
                Assert.Equal(0, driver.GetModeSetCount(pinNumber));
            }

            Assert.Equal(ServiceStatus.Running, subject.Pins[17].Status);
            Assert.Equal(PinMode.Output, driver.GetMode(17));
            Assert.Equal(["create MCP3008 21"], adcFactory.Events);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenMcp3008IsRemovedOnReload_ThenItsPinsOpenAsGpioPins()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var (subject, serializer) = CreateSubject(driver);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject, Mcp3008PinNumbers);

            // Act
            serializer.UpdateConfiguration(subject, JsonWithoutMcp3008);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Null(subject.Mcp3008);
            foreach (var pinNumber in Mcp3008PinNumbers)
            {
                var pin = subject.Pins[pinNumber];
                Assert.Equal(pinNumber, pin.PinNumber);
                Assert.Equal(ServiceStatus.Running, pin.Status);
                Assert.Null(pin.StatusMessage);
                Assert.Equal(PinMode.Input, driver.GetMode(pinNumber));
                Assert.Equal(1, driver.GetModeSetCount(pinNumber));
            }
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenMcp3008IsAddedOnReload_ThenItsPinsAreDetachedAndReserved()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var (subject, serializer) = CreateSubject(driver);
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject);
            var pinsBeforeReload = Mcp3008PinNumbers.Select(pinNumber => subject.Pins[pinNumber]).ToArray();
            var modeSetCountsBeforeReload = Mcp3008PinNumbers.Select(driver.GetModeSetCount).ToArray();

            // Act
            serializer.UpdateConfiguration(subject, JsonWithMcp3008);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.All(pinsBeforeReload, pin =>
            {
                Assert.Equal(ServiceStatus.Stopped, pin.Status);
                Assert.Null(pin.Controller);
            });
            Assert.Equal(modeSetCountsBeforeReload, Mcp3008PinNumbers.Select(driver.GetModeSetCount));
            foreach (var pinNumber in Mcp3008PinNumbers)
            {
                var pin = subject.Pins[pinNumber];
                Assert.Equal(pinNumber, pin.PinNumber);
                Assert.Equal(ServiceStatus.Unavailable, pin.Status);
                Assert.Equal("Used by the MCP3008", pin.StatusMessage);
            }
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenMcp3008ClockPinMovesOnReload_ThenOldAdcIsDisposedBeforeTheFreedPinOpensAndTheNewPinClosesBeforeTheNewAdcIsCreated()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var (subject, serializer) = CreateSubject(driver, adcFactory: adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject, Mcp3008PinNumbers);
            var events = new List<string>();
            driver.PinOpened = pinNumber => events.Add($"open {pinNumber}");
            driver.PinClosed = pinNumber => events.Add($"close {pinNumber}");
            adcFactory.EventRecorded = events.Add;

            // Act
            serializer.UpdateConfiguration(subject, JsonWithMovedMcp3008ClockPin);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Subset(new HashSet<string> { "dispose MCP3008 21", "open 21", "close 26", "create MCP3008 26" }, events.ToHashSet());
            Assert.True(events.IndexOf("dispose MCP3008 21") < events.IndexOf("open 21"));
            Assert.True(events.IndexOf("close 26") < events.IndexOf("create MCP3008 26"));
            Assert.Equal(1, driver.GetCloseCount(26));

            var freedPin = subject.Pins[21];
            Assert.Equal(ServiceStatus.Running, freedPin.Status);
            Assert.Equal(PinMode.Input, driver.GetMode(21));

            var reservedPin = subject.Pins[26];
            Assert.Equal(26, reservedPin.PinNumber);
            Assert.Equal(ServiceStatus.Unavailable, reservedPin.Status);
            Assert.Equal("Used by the MCP3008", reservedPin.StatusMessage);
            Assert.Null(reservedPin.Controller);

            Assert.Equal(8, subject.AnalogChannels.Count);
            Assert.All(subject.AnalogChannels.Values, channel => Assert.Equal(ServiceStatus.Running, channel.Status));
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenPinDiscoveryThrowsDuringAnAdcChange_ThenTheNextApplyCreatesTheAdc()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var (subject, serializer) = CreateSubject(driver, adcFactory: adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject, Mcp3008PinNumbers);
            driver.FailingCallbackRemovalPinNumber = 26;
            serializer.UpdateConfiguration(subject, JsonWithMovedMcp3008ClockPin);
            await Assert.ThrowsAsync<IOException>(() => subject.ApplyConfigurationAsync(CancellationToken.None));
            Assert.Equal(1, adcFactory.Count("dispose MCP3008 21"));
            Assert.Equal(0, adcFactory.Count("create MCP3008 26"));
            Assert.Empty(subject.AnalogChannels);

            // Act
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal(1, adcFactory.Count("create MCP3008 26"));
            Assert.Equal(ServiceStatus.Unavailable, subject.Pins[26].Status);
            Assert.Equal("Used by the MCP3008", subject.Pins[26].StatusMessage);
            Assert.Equal(ServiceStatus.Running, subject.Pins[21].Status);
            Assert.Equal(8, subject.AnalogChannels.Count);
            Assert.Null(subject.StatusMessage);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenAPinOfANewAdcCannotBeClosed_ThenThePinReportsItAndTheAdcIsCreated()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var (subject, serializer) = CreateSubject(driver, adcFactory: adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject, Mcp3008PinNumbers);
            driver.FailingClosePinNumber = 26;

            // Act
            serializer.UpdateConfiguration(subject, JsonWithMovedMcp3008ClockPin);
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            var pin = subject.Pins[26];
            Assert.Equal(ServiceStatus.Unavailable, pin.Status);
            Assert.Equal("Used by the MCP3008, but the pin could not be closed: Pin 26 could not be closed.", pin.StatusMessage);
            Assert.Equal(1, adcFactory.Count("create MCP3008 26"));
            Assert.Equal(8, subject.AnalogChannels.Count);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenAds1115IsConfiguredOnI2cBus1_ThenGpio2And3AreReserved()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var (subject, _) = CreateSubject(driver);
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject, [2, 3]);

            // Assert
            foreach (var pinNumber in new[] { 2, 3 })
            {
                var pin = subject.Pins[pinNumber];
                Assert.Equal(pinNumber, pin.PinNumber);
                Assert.Equal("Used by I2C (ADS1115)", pin.StatusMessage);
                Assert.Equal(0, driver.GetOpenCount(pinNumber));
                Assert.Equal(0, driver.GetModeSetCount(pinNumber));
            }
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenAds1115IsConfiguredOnOtherI2cBus_ThenGpio2And3AreGpioPins()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var (subject, _) = CreateSubject(driver);
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 0 };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await WaitUntilAllPinsRunAsync(subject);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenPinsAreReplacedWhileTheyAreDiscovered_ThenTheReplacedPinsAreAttached()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var (subject, _) = CreateSubject(driver);
        var pinBeforeReplacement = subject.Pins[17];
        var replacementPin = new GpioPin { Mode = GpioPinMode.InputPullUp };
        var isReplaced = false;
        driver.PinModeSet = pinNumber =>
        {
            // Runs on the service loop while it discovers the pins, the way a reload replaces them without the lock.
            if (pinNumber == 5 && !isReplaced)
            {
                isReplaced = true;
                subject.Pins = new Dictionary<int, GpioPin> { [17] = replacementPin };
            }
        };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAllPinsRunAsync(subject);

            // Assert
            Assert.True(isReplaced);
            Assert.Same(replacementPin, subject.Pins[17]);
            Assert.Equal(17, replacementPin.PinNumber);
            Assert.Equal(PinMode.InputPullUp, driver.GetMode(17));
            Assert.Equal(ServiceStatus.Stopped, pinBeforeReplacement.Status);
            Assert.All(subject.Pins, entry => Assert.Equal(entry.Key, entry.Value.PinNumber));
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    // Every subject gets ADCs that touch no hardware: a real MCP3008 or ADS1115 reaches the native GPIO and I2C drivers.
    private static (GpioSubject Subject, ConfigurableSubjectSerializer Serializer) CreateSubject(
        GpioDriver driver, bool isRaspberryPi = false, RecordingAdcFactory? adcFactory = null)
    {
        adcFactory ??= new RecordingAdcFactory();

        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(context);

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(GpioSubject).Assembly);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider());

        var subject = new GpioSubject(driver, isRaspberryPi, adcFactory.CreateMcp3008, adcFactory.CreateAds1115)
        {
            // Long, so that a poll does not correct what the reload did.
            PollingInterval = TimeSpan.FromHours(1),
            Pins = new Dictionary<int, GpioPin>
            {
                [5] = new() { Mode = GpioPinMode.Output },
                [17] = new() { Mode = GpioPinMode.InputPullDown },
                [27] = new() { Mode = GpioPinMode.InputPullDown }
            }
        };
        ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        return (subject, serializer);
    }

    private static Task WaitUntilAllPinsRunAsync(GpioSubject subject, int[]? reservedPinNumbers = null) =>
        AsyncTestHelpers.WaitUntilAsync(() =>
            subject.Status == ServiceStatus.Running &&
            subject.Pins.Count == 28 &&
            subject.Pins.All(entry => entry.Value.Status ==
                (reservedPinNumbers?.Contains(entry.Key) == true ? ServiceStatus.Unavailable : ServiceStatus.Running)));
}
