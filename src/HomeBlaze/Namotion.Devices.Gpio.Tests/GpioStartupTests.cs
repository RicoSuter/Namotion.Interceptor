using System.Device.Gpio;
using HomeBlaze.Abstractions;
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
