using System.Device.Gpio;
using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Devices.Gpio.Simulation;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioRaspberryPiDefaultsTests
{
    [Theory]
    [InlineData(0, "ID_SD (HAT EEPROM)")]
    [InlineData(1, "ID_SC (HAT EEPROM)")]
    [InlineData(2, "I2C1 SDA")]
    [InlineData(3, "I2C1 SCL")]
    [InlineData(4, "GPCLK0")]
    [InlineData(7, "SPI0 CE1")]
    [InlineData(8, "SPI0 CE0")]
    [InlineData(9, "SPI0 MISO")]
    [InlineData(10, "SPI0 MOSI")]
    [InlineData(11, "SPI0 SCLK")]
    [InlineData(12, "PWM0")]
    [InlineData(13, "PWM1")]
    [InlineData(14, "UART TX")]
    [InlineData(15, "UART RX")]
    [InlineData(18, "PCM CLK / PWM0")]
    public async Task WhenPinIsUnconfigured_ThenFixedFunctionPinGetsItsName(int pinNumber, string expectedName)
    {
        // Arrange
        var subject = CreateSubject(28);

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedName, subject.Pins[pinNumber].Name);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(27)]
    public async Task WhenPinIsGeneralPurpose_ThenItStaysWithoutName(int pinNumber)
    {
        // Arrange
        var subject = CreateSubject(28);

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Null(subject.Pins[pinNumber].Name);
    }

    [Fact]
    public async Task WhenPinsAreUnconfiguredInputs_ThenPowerOnPullsAreApplied()
    {
        // Arrange
        var subject = CreateSubject(28);

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[0].Mode);
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[8].Mode);
        Assert.Equal(GpioPinMode.InputPullDown, subject.Pins[9].Mode);
        Assert.Equal(GpioPinMode.InputPullDown, subject.Pins[27].Mode);
    }

    [Fact]
    public async Task WhenPinIsNamedOutputOrHasPull_ThenItIsNotChanged()
    {
        // Arrange
        var subject = CreateSubject(28);
        subject.Pins[2].Name = "Sensor";
        subject.Pins[4].Mode = GpioPinMode.Output;
        subject.Pins[17].Mode = GpioPinMode.InputPullUp;

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Sensor", subject.Pins[2].Name);
        Assert.Equal(GpioPinMode.Input, subject.Pins[2].Mode);
        Assert.Equal(GpioPinMode.Output, subject.Pins[4].Mode);
        Assert.Null(subject.Pins[4].Name);
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[17].Mode);
        Assert.Null(subject.Pins[17].Name);
    }

    [Fact]
    public async Task WhenAppliedTwice_ThenTheSecondRunChangesNothing()
    {
        // Arrange
        var subject = CreateSubject(28);
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);
        var expected = subject.Pins.ToDictionary(pin => pin.Key, pin => (pin.Value.Name, pin.Value.Mode));

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expected, subject.Pins.ToDictionary(pin => pin.Key, pin => (pin.Value.Name, pin.Value.Mode)));
    }

    [Fact]
    public async Task WhenBoardHasMorePins_ThenPinsAbove27AreNotChanged()
    {
        // Arrange
        var subject = CreateSubject(40);

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(GpioPinMode.Input, subject.Pins[30].Mode);
        Assert.Null(subject.Pins[30].Name);
    }

    [Fact]
    public async Task WhenSubjectIsRunning_ThenPullIsAppliedToTheHardware()
    {
        // Arrange
        var driver = new SimulationGpioDriver();
        var subject = GpioTestSubject.Create(driver);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PinMode.InputPullUp, driver.PinModes[4]);
        Assert.Equal(PinMode.InputPullDown, driver.PinModes[17]);
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[4].Mode);
        Assert.Equal(GpioPinMode.InputPullDown, subject.Pins[17].Mode);
    }

    [Fact]
    public async Task WhenMcp3008IsConfigured_ThenItsPinsAreNotChanged()
    {
        // Arrange
        var subject = CreateSubject(28);
        subject.Mcp3008 = new Mcp3008Configuration { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        foreach (var pinNumber in new[] { 16, 19, 20, 21 })
        {
            Assert.Equal(GpioPinMode.Input, subject.Pins[pinNumber].Mode);
            Assert.Null(subject.Pins[pinNumber].Name);
        }

        Assert.Equal(GpioPinMode.InputPullDown, subject.Pins[17].Mode);
    }

    [Fact]
    public async Task WhenPinsChange_ThenConfigurationIsWritten()
    {
        // Arrange
        var subject = CreateSubject(28);
        var writer = CreateWriter(subject);

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        var writtenSubject = Assert.Single(writer.WrittenSubjects);
        Assert.Same(subject, writtenSubject);
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[0].Mode);
    }

    [Fact]
    public async Task WhenNothingChanges_ThenConfigurationIsNotWritten()
    {
        // Arrange
        var subject = CreateSubject(28);
        var writer = CreateWriter(subject);
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);
        writer.WrittenSubjects.Clear();

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Empty(writer.WrittenSubjects);
    }

    [Fact]
    public async Task WhenWriterWritesNothing_ThenOperationThrows()
    {
        // Arrange
        var subject = CreateSubject(28);
        var writer = CreateWriter(subject);
        writer.IsWriting = false;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None));
        Assert.Equal("The Raspberry Pi defaults were applied but the configuration could not be saved.", exception.Message);
        Assert.Single(writer.WrittenSubjects);
    }

    [Fact]
    public async Task WhenModeChangeOfALaterPinThrows_ThenEarlierPinsAreWrittenAndTheExceptionPropagates()
    {
        // Arrange
        var subject = CreateSubject(28);
        var writer = CreateWriter(subject);
        using var controller = new GpioController(PinNumberingScheme.Logical, new SimulationGpioDriver());
        var failingPin = subject.Pins[9];
        failingPin.Controller = controller; // pin 9 is not open, so setting its mode throws
        failingPin.Status = ServiceStatus.Running;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None));
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[0].Mode);
        Assert.Equal(GpioPinMode.Input, failingPin.Mode);
        Assert.Same(subject, Assert.Single(writer.WrittenSubjects));
    }

    [Fact]
    public async Task WhenModeChangeThrowsAndWriterWritesNothing_ThenTheModeChangeExceptionPropagates()
    {
        // Arrange
        var subject = CreateSubject(28);
        var writer = CreateWriter(subject);
        writer.IsWriting = false;
        using var controller = new GpioController(PinNumberingScheme.Logical, new SimulationGpioDriver());
        subject.Pins[9].Controller = controller;
        subject.Pins[9].Status = ServiceStatus.Running;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None));
        Assert.Contains("Failed to change GPIO pin 9 mode", exception.Message);
        Assert.Single(writer.WrittenSubjects);
    }

    [Theory]
    [InlineData(4, GpioPinMode.InputPullUp, PinMode.InputPullUp)]
    [InlineData(17, GpioPinMode.InputPullDown, PinMode.InputPullDown)]
    public async Task WhenPinIsDiscoveredOnRaspberryPi_ThenItStartsWithItsPowerOnPull(int pinNumber, GpioPinMode expectedMode, PinMode expectedDriverMode)
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver, isRaspberryPi: true);

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Assert
        Assert.Equal(expectedMode, subject.Pins[pinNumber].Mode);
        Assert.Equal(expectedDriverMode, driver.GetMode(pinNumber));
        Assert.Null(subject.Pins[pinNumber].Name);
    }

    [Fact]
    public async Task WhenConfiguredPinIsPlainInputOnRaspberryPi_ThenItStaysPlainInput()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver, isRaspberryPi: true, pins: new Dictionary<int, GpioPin>
        {
            [17] = new() { Mode = GpioPinMode.Input }
        });

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Assert
        Assert.Equal(GpioPinMode.Input, subject.Pins[17].Mode);
        Assert.Equal(PinMode.Input, driver.GetMode(17));
        Assert.Equal(GpioPinMode.InputPullDown, subject.Pins[16].Mode);
    }

    [Fact]
    public async Task WhenPinIsDiscoveredOnOtherBoard_ThenItStartsAsPlainInput()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver, isRaspberryPi: false);

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Assert
        foreach (var pinNumber in new[] { 4, 17 })
        {
            Assert.Equal(GpioPinMode.Input, subject.Pins[pinNumber].Mode);
            Assert.Equal(PinMode.Input, driver.GetMode(pinNumber));
        }
    }

    [Fact]
    public async Task WhenPinsWereDiscoveredOnRaspberryPi_ThenFixedFunctionPinsGetTheirNames()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver, isRaspberryPi: true);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject);

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Equal("I2C1 SDA", subject.Pins[2].Name);
        Assert.Equal("SPI0 MISO", subject.Pins[9].Name);
        Assert.Equal("PCM CLK / PWM0", subject.Pins[18].Name);
        Assert.Null(subject.Pins[17].Name);
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[2].Mode);
        Assert.Equal(GpioPinMode.InputPullDown, subject.Pins[17].Mode);
    }

    [Fact]
    public async Task WhenUnnamedPinIsInItsPowerOnPull_ThenItIsUnconfiguredAndGetsItsName()
    {
        // Arrange
        var subject = CreateSubject(28);
        subject.Pins[2].Mode = GpioPinMode.InputPullUp;
        subject.Pins[9].Mode = GpioPinMode.InputPullDown;
        subject.Pins[10].Mode = GpioPinMode.InputPullUp;

        // Act
        await subject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

        // Assert
        Assert.Equal("I2C1 SDA", subject.Pins[2].Name);
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[2].Mode);
        Assert.Equal("SPI0 MISO", subject.Pins[9].Name);
        Assert.Equal(GpioPinMode.InputPullDown, subject.Pins[9].Mode);
        Assert.Null(subject.Pins[10].Name);
        Assert.Equal(GpioPinMode.InputPullUp, subject.Pins[10].Mode);
    }

    private static TestGpioConfigurationWriter CreateWriter(GpioSubject subject)
    {
        var context = InterceptorSubjectContext.Create().WithParents();
        return new TestGpioConfigurationWriter(context) { Gpio = subject };
    }

    private static GpioSubject CreateSubject(int pinCount)
    {
        return new GpioSubject
        {
            Pins = Enumerable.Range(0, pinCount).ToDictionary(pinNumber => pinNumber, _ => new GpioPin())
        };
    }
}

[InterceptorSubject]
public partial class TestGpioConfigurationWriter : IConfigurationWriter
{
    public bool IsWriting { get; set; } = true;

    public List<IInterceptorSubject> WrittenSubjects { get; } = [];

    public partial GpioSubject? Gpio { get; set; }

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        WrittenSubjects.Add(subject);
        return Task.FromResult(IsWriting);
    }
}
