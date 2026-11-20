using System.Collections.Concurrent;
using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioStartupTests
{
    [Fact]
    public async Task WhenTheServiceStops_ThenEveryPinIsReleasedAndAnAppliedConfigurationOpensNone()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var closedPinNumbers = new ConcurrentBag<int>();
        driver.PinClosed = closedPinNumbers.Add;
        var subject = GpioTestSubject.Create(driver, pins: new Dictionary<int, GpioPin>
        {
            [17] = new() { Mode = GpioPinMode.Output },
            [22] = new() { Mode = GpioPinMode.Input }
        });
        await subject.StartAsync(CancellationToken.None);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Act
        await subject.StopAsync(CancellationToken.None);
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.Contains(17, closedPinNumbers);
        Assert.Contains(22, closedPinNumbers);
        Assert.All(subject.Pins.Values, pin => Assert.Equal(ServiceStatus.Stopped, pin.Status));
        Assert.Equal(1, driver.GetOpenCount(17));
        Assert.Equal(1, driver.GetOpenCount(22));

        subject.Dispose();
    }

    [Fact]
    public async Task WhenPollingFailsAndThenSucceeds_ThenTheStatusReturnsToRunning()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver);
        subject.PollingInterval = TimeSpan.FromMilliseconds(10);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);
        driver.FailsReads = true;
        await AsyncTestHelpers.WaitUntilAsync(() =>
            subject.Status == ServiceStatus.Error && subject.StatusMessage?.StartsWith("Polling error: ") == true);

        // Act
        driver.FailsReads = false;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.StatusMessage is null);
    }

    [Fact]
    public async Task WhenPollingFailsAndThenSucceedsAfterAnAdcFailedToStart_ThenTheStatusReportsTheAdcAgain()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory { FailsNextMcp3008Creation = true };
        var subject = GpioTestSubject.Create(driver, adcFactory: adcFactory);
        subject.Mcp3008 = GpioTestSubject.CreateMcp3008Configuration();
        subject.PollingInterval = TimeSpan.FromMilliseconds(10);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject, GpioTestSubject.Mcp3008PinNumbers);
        driver.FailsReads = true;
        await AsyncTestHelpers.WaitUntilAsync(() =>
            subject.Status == ServiceStatus.Error && subject.StatusMessage?.StartsWith("Polling error: ") == true);

        // Act
        driver.FailsReads = false;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running);
        Assert.Equal("MCP3008 init failed: No MCP3008 answered.", subject.StatusMessage);
    }

    [Fact]
    public async Task WhenAppliedConfigurationEnabledSimulation_ThenStartKeepsItsController()
    {
        // Arrange
        var subject = new GpioSubject { UseSimulation = true };
        await subject.ApplyConfigurationAsync(CancellationToken.None);
        var controller = subject.Pins[4].Controller;

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Assert
        Assert.NotNull(controller);
        Assert.All(subject.Pins.Values, pin => Assert.Same(controller, pin.Controller));
    }

    [Fact]
    public async Task WhenInvertedOutputStartsAfterReboot_ThenItKeepsThePowerOnPull()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Input, inputLevel: PinValue.High, latch: PinValue.Low);
        var subject = CreateSubject(driver, isInverted: true);

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Assert
        Assert.DoesNotContain(PinValue.Low, driver.GetDrivenLevels(5));
        Assert.Equal(PinValue.High, driver.GetLatch(5));
        Assert.False(subject.Pins[5].Value);
        Assert.False(subject.Pins[5].IsOn);
    }

    [Fact]
    public async Task WhenInvertedOutputStartsAfterRestart_ThenItKeepsTheDrivenLevel()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Output, inputLevel: PinValue.High, latch: PinValue.Low);
        var subject = CreateSubject(driver, isInverted: true);

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Assert
        Assert.DoesNotContain(PinValue.High, driver.GetDrivenLevels(5));
        Assert.Equal(PinValue.Low, driver.GetLatch(5));
        Assert.True(subject.Pins[5].Value);
        Assert.True(subject.Pins[5].IsOn);
    }

    [Fact]
    public async Task WhenOutputStartsAfterRebootWithLowPull_ThenItKeepsTheLowLevel()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        driver.SetUpPin(5, PinMode.Input, inputLevel: PinValue.Low, latch: PinValue.High);
        var subject = CreateSubject(driver, isInverted: false);

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Assert
        Assert.DoesNotContain(PinValue.High, driver.GetDrivenLevels(5));
        Assert.Equal(PinValue.Low, driver.GetLatch(5));
        Assert.False(subject.Pins[5].Value);
    }

    [Fact]
    public async Task WhenDriverCannotReadBeforeModeIsSet_ThenOutputStartsWithTheLevelReadBack()
    {
        // Arrange
        var driver = new LatchingGpioDriver { FailsReadBeforeMode = true };
        driver.SetUpPin(5, PinMode.Input, inputLevel: PinValue.High, latch: PinValue.Low);
        var subject = CreateSubject(driver, isInverted: true);

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running && subject.Pins[5].Status != ServiceStatus.Stopped);

        // Assert
        Assert.Equal(ServiceStatus.Running, subject.Pins[5].Status);
        Assert.Equal(PinMode.Output, driver.GetMode(5));
        Assert.Equal(PinValue.Low, driver.GetLatch(5));
        Assert.True(subject.Pins[5].Value);
    }

    private static GpioSubject CreateSubject(LatchingGpioDriver driver, bool isInverted) =>
        GpioTestSubject.Create(driver, pins: new Dictionary<int, GpioPin>
        {
            [5] = new() { Mode = GpioPinMode.Output, IsInverted = isInverted }
        });
}
