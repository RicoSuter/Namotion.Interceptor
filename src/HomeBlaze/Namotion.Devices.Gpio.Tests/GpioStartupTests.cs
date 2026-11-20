using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Iot.Device.Ads1115;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioStartupTests
{
    [Fact]
    public async Task WhenAppliedConfigurationEnabledSimulation_ThenStartReusesItsController()
    {
        // Arrange
        var subject = new GpioSubject { UseSimulation = true };
        await subject.ApplyConfigurationAsync(CancellationToken.None);
        var controller = subject.Pins[4].Controller;
        var replacementPin = new GpioPin();
        subject.Pins = new Dictionary<int, GpioPin>(subject.Pins) { [17] = replacementPin };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && replacementPin.Status == ServiceStatus.Running);

            // Assert
            Assert.NotNull(controller);
            Assert.Same(controller, replacementPin.Controller);
            Assert.Same(controller, subject.Pins[4].Controller);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenInvertedOutputStartsAfterReboot_ThenItKeepsThePowerOnPull()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Input, inputLevel: PinValue.High, latch: PinValue.Low);
        var subject = CreateSubject(driver, isInverted: true);

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.Pins[5].Status == ServiceStatus.Running);

            // Assert
            Assert.DoesNotContain(PinValue.Low, driver.GetDrivenLevels(5));
            Assert.Equal(PinValue.High, driver.GetLatch(5));
            Assert.False(subject.Pins[5].Value);
            Assert.False(subject.Pins[5].IsOn);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenInvertedOutputStartsAfterRestart_ThenItKeepsTheDrivenLevel()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Output, inputLevel: PinValue.High, latch: PinValue.Low);
        var subject = CreateSubject(driver, isInverted: true);

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.Pins[5].Status == ServiceStatus.Running);

            // Assert
            Assert.DoesNotContain(PinValue.High, driver.GetDrivenLevels(5));
            Assert.Equal(PinValue.Low, driver.GetLatch(5));
            Assert.True(subject.Pins[5].Value);
            Assert.True(subject.Pins[5].IsOn);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenOutputStartsAfterRebootWithLowPull_ThenItKeepsTheLowLevel()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Input, inputLevel: PinValue.Low, latch: PinValue.High);
        var subject = CreateSubject(driver, isInverted: false);

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.Pins[5].Status == ServiceStatus.Running);

            // Assert
            Assert.DoesNotContain(PinValue.High, driver.GetDrivenLevels(5));
            Assert.Equal(PinValue.Low, driver.GetLatch(5));
            Assert.False(subject.Pins[5].Value);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenDriverCannotReadBeforeModeIsSet_ThenOutputStartsWithTheLevelReadBack()
    {
        // Arrange
        var driver = new LatchingGpioDriver { FailsReadBeforeMode = true };
        driver.SetUpPin(5, PinMode.Input, inputLevel: PinValue.High, latch: PinValue.Low);
        var subject = CreateSubject(driver, isInverted: true);

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.Pins[5].Status != ServiceStatus.Stopped);

            // Assert
            Assert.Equal(ServiceStatus.Running, subject.Pins[5].Status);
            Assert.Equal(PinMode.Output, driver.GetMode(5));
            Assert.Equal(PinValue.Low, driver.GetLatch(5));
            Assert.True(subject.Pins[5].Value);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenMcp3008IsConfigured_ThenStartCreatesIt()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateAdcSubject(new LatchingGpioDriver(), adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.StatusMessage is null);

            // Assert
            Assert.Equal(["create MCP3008 21"], adcFactory.Events);
            Assert.Equal(Enumerable.Range(0, 8), subject.AnalogChannels.Keys.Order());
            Assert.All(subject.AnalogChannels.Values, channel =>
            {
                Assert.Equal(AdcSource.Mcp3008, channel.Source);
                Assert.Equal(ServiceStatus.Running, channel.Status);
            });
            Assert.Equal("Used by the MCP3008", subject.Pins[21].StatusMessage);
            Assert.Null(subject.StatusMessage);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenMcp3008IsAppliedBeforeStart_ThenStartCreatesIt()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateAdcSubject(new LatchingGpioDriver(), adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        await subject.ApplyConfigurationAsync(CancellationToken.None);
        Assert.Empty(adcFactory.Events);
        Assert.Empty(subject.AnalogChannels);

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running);

            // Assert
            Assert.Equal(["create MCP3008 21"], adcFactory.Events);
            Assert.Equal(8, subject.AnalogChannels.Count);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenBothAdcsAreConfigured_ThenStartCreatesBoth()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateAdcSubject(driver, adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.StatusMessage is null);

            // Assert
            Assert.Equal(["create MCP3008 21", "create ADS1115 1"], adcFactory.Events);
            Assert.Equal(Enumerable.Range(0, 12), subject.AnalogChannels.Keys.Order());
            Assert.All(Enumerable.Range(8, 4), key => Assert.Equal(AdcSource.Ads1115, subject.AnalogChannels[key].Source));
            foreach (var pinNumber in new[] { 2, 3, 16, 19, 20, 21 })
            {
                Assert.Equal(ServiceStatus.Unavailable, subject.Pins[pinNumber].Status);
                Assert.Equal(0, driver.GetOpenCount(pinNumber));
            }

            Assert.Null(subject.StatusMessage);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenMcp3008CannotBeCreatedAtStart_ThenTheNextApplyCreatesIt()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory { FailsNextMcp3008Creation = true };
        var subject = CreateAdcSubject(new LatchingGpioDriver(), adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() =>
                subject.Status == ServiceStatus.Running && subject.StatusMessage == "MCP3008 init failed: No MCP3008 answered.");
            Assert.Empty(subject.AnalogChannels);

            // Act
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal(["create MCP3008 21"], adcFactory.Events);
            Assert.Equal(8, subject.AnalogChannels.Count);
            Assert.Null(subject.StatusMessage);
            Assert.Equal(ServiceStatus.Running, subject.Status);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenOnlyTheAds1115ConfigurationChanges_ThenTheMcp3008IsKept()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateAdcSubject(new LatchingGpioDriver(), adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.StatusMessage is null);

            // Act
            subject.Ads1115 = new Ads1115Configuration { I2cBus = 1, Address = 0x49 };
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal(["create MCP3008 21", "create ADS1115 1", "dispose ADS1115 1", "create ADS1115 1"], adcFactory.Events);
            Assert.Equal(Enumerable.Range(0, 12), subject.AnalogChannels.Keys.Order());
            Assert.Null(subject.StatusMessage);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenMcp3008KeepsFailing_ThenTheAds1115IsKept()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory { FailsNextMcp3008Creation = true };
        var subject = CreateAdcSubject(new LatchingGpioDriver(), adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() =>
                subject.Status == ServiceStatus.Running && subject.StatusMessage == "MCP3008 init failed: No MCP3008 answered.");
            adcFactory.FailsNextMcp3008Creation = true;

            // Act
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal(["create ADS1115 1"], adcFactory.Events);
            Assert.Equal(Enumerable.Range(8, 4), subject.AnalogChannels.Keys.Order());
            Assert.Equal("MCP3008 init failed: No MCP3008 answered.", subject.StatusMessage);
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenAdcsAreAppliedWithoutController_ThenNoneIsCreated()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateAdcSubject(new LatchingGpioDriver(), adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };
        try
        {
            // Act
            await subject.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Empty(adcFactory.Events);
            Assert.Empty(subject.AnalogChannels);
        }
        finally
        {
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenAds1115ChannelsArePolled_ThenTheSingleEndedInputsAreSelected()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateAdcSubject(new LatchingGpioDriver(), adcFactory);
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };

        // Act
        await subject.StartAsync(CancellationToken.None);
        try
        {
            // The fake reads all bits set, so a polled channel reads -1.
            await AsyncTestHelpers.WaitUntilAsync(() =>
                subject.AnalogChannels.Count == 4 && subject.AnalogChannels.Values.All(channel => channel.RawValue == -1));

            // Assert
            Assert.Equal(
                [InputMultiplexer.AIN0, InputMultiplexer.AIN1, InputMultiplexer.AIN2, InputMultiplexer.AIN3],
                adcFactory.Ads1115InputMultiplexers.Distinct());
            Assert.All(subject.AnalogChannels.Values, channel => Assert.Equal(ServiceStatus.Running, channel.Status));
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    [Fact]
    public async Task WhenAdcIsDisposedDuringARead_ThenTheDisposalWaitsForTheRead()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateAdcSubject(new LatchingGpioDriver(), adcFactory);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };

        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readGate = new TaskCompletionSource();
        var readCompleted = new TaskCompletionSource();
        adcFactory.Mcp3008Transferring = () =>
        {
            readStarted.TrySetResult();
            readGate.Task.Wait();
            readCompleted.TrySetResult();
        };

        bool? isReadCompletedAtDisposal = null;
        adcFactory.EventRecorded = recordedEvent =>
        {
            if (recordedEvent == "dispose MCP3008 21")
            {
                isReadCompletedAtDisposal = readCompleted.Task.IsCompleted;
            }
        };

        // The poll runs on a thread pool thread, which the gate blocks for the short bound below.
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await readStarted.Task.WaitAsync(TestTimeouts.Default);
            subject.Mcp3008 = null;

            // Act
            var applyTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => subject.ApplyConfigurationAsync(CancellationToken.None));
            await Assert.ThrowsAsync<TimeoutException>(() => applyTask.WaitAsync(TimeSpan.FromMilliseconds(200)));
            readGate.SetResult();
            await applyTask.WaitAsync(TestTimeouts.Default);

            // Assert
            Assert.True(isReadCompletedAtDisposal);
            Assert.Empty(subject.AnalogChannels);
        }
        finally
        {
            readGate.TrySetResult();
            await subject.StopAsync(CancellationToken.None);
            subject.Dispose();
        }
    }

    // ADCs that touch no hardware: a real MCP3008 or ADS1115 reaches the native GPIO and I2C drivers.
    private static GpioSubject CreateAdcSubject(GpioDriver driver, RecordingAdcFactory adcFactory)
    {
        return new GpioSubject(driver, isRaspberryPi: false, adcFactory.CreateMcp3008, adcFactory.CreateAds1115)
        {
            // Long, so that the test reads the state the start left.
            PollingInterval = TimeSpan.FromHours(1)
        };
    }

    private static GpioSubject CreateSubject(GpioDriver driver, bool isInverted)
    {
        return new GpioSubject(driver)
        {
            Pins = new Dictionary<int, GpioPin>
            {
                [5] = new() { Mode = GpioPinMode.Output, IsInverted = isInverted }
            }
        };
    }
}
