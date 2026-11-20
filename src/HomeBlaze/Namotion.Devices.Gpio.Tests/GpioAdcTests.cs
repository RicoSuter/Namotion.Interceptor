using System.ComponentModel;
using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Iot.Device.Ads1115;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// The MCP3008 and ADS1115 exist between the start of the GPIO hardware handling and its stop, which every apply of a
/// configuration does before it starts again.
/// </summary>
public class GpioAdcTests
{
    private static readonly int[] AdcPinNumbers = [2, 3, 16, 19, 20, 21];

    [Fact]
    public async Task WhenAdcsAreConfigured_ThenStartCreatesThemAndReservesTheirPins()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateSubjectWithBothAdcs(driver, adcFactory);

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject, AdcPinNumbers);

        // Assert
        Assert.Equal(["create MCP3008 21", "create ADS1115 1"], adcFactory.Events);
        Assert.Equal(Enumerable.Range(0, 12), subject.AnalogChannels.Keys.Order());
        Assert.All(Enumerable.Range(0, 8), key => Assert.Equal(AdcSource.Mcp3008, subject.AnalogChannels[key].Source));
        Assert.All(Enumerable.Range(8, 4), key => Assert.Equal(AdcSource.Ads1115, subject.AnalogChannels[key].Source));
        Assert.All(subject.AnalogChannels.Values, channel => Assert.Equal(ServiceStatus.Running, channel.Status));
        AssertAdcPinsAreReserved(subject, driver);
        Assert.Null(subject.StatusMessage);
    }

    [Fact]
    public async Task WhenConfigurationIsReloaded_ThenAdcsAreDisposedAndCreatedAgainAndTheirPinsAreNeverOpened()
    {
        // Arrange
        const string json = """
            {
              "$type": "Namotion.Devices.Gpio.GpioSubject",
              "mcp3008": { "clockPin": 21, "mosiPin": 20, "misoPin": 19, "chipSelectPin": 16 },
              "ads1115": { "i2cBus": 1 },
              "pins": {
                "17": { "name": "Led", "mode": "Output" }
              }
            }
            """;
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateSubjectWithBothAdcs(driver, adcFactory);
        var serializer = GpioTestSubject.CreateSerializer(subject);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject, AdcPinNumbers);

        // Act
        serializer.UpdateConfiguration(subject, json);
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(
            ["create MCP3008 21", "create ADS1115 1", "dispose MCP3008 21", "dispose ADS1115 1", "create MCP3008 21", "create ADS1115 1"],
            adcFactory.Events);
        Assert.Equal(12, subject.AnalogChannels.Count);
        AssertAdcPinsAreReserved(subject, driver);
        Assert.Equal(ServiceStatus.Running, subject.Pins[17].Status);
        Assert.Null(subject.StatusMessage);
    }

    [Fact]
    public async Task WhenAdcIsRemovedWithNull_ThenItIsDisposedAndItsPinsOpenAsGpioPins()
    {
        // Arrange
        const string jsonWithoutMcp3008 = """
            {
              "$type": "Namotion.Devices.Gpio.GpioSubject",
              "mcp3008": null
            }
            """;
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var subject = GpioTestSubject.Create(driver, adcFactory: adcFactory);
        subject.Mcp3008 = GpioTestSubject.CreateMcp3008Configuration();
        var serializer = GpioTestSubject.CreateSerializer(subject);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject, GpioTestSubject.Mcp3008PinNumbers);

        // Act
        serializer.UpdateConfiguration(subject, jsonWithoutMcp3008);
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Null(subject.Mcp3008);
        Assert.Equal(["create MCP3008 21", "dispose MCP3008 21"], adcFactory.Events);
        Assert.Empty(subject.AnalogChannels);
        foreach (var pinNumber in GpioTestSubject.Mcp3008PinNumbers)
        {
            var pin = subject.Pins[pinNumber];
            Assert.Equal(ServiceStatus.Running, pin.Status);
            Assert.Null(pin.StatusMessage);
            Assert.Equal(PinMode.Input, driver.GetMode(pinNumber));
        }
    }

    [Theory]
    [InlineData(true, false, 1, true)]
    [InlineData(false, true, 1, true)]
    [InlineData(false, false, 1, false)]
    [InlineData(true, false, 0, false)]
    public async Task WhenAds1115IsConfigured_ThenGpio2And3AreReservedOnlyForI2cBus1OfARaspberryPi(
        bool isRaspberryPi, bool isRaspberryPiBoard, int i2cBus, bool isReserved)
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var subject = GpioTestSubject.Create(driver, isRaspberryPi, isRaspberryPiBoard: isRaspberryPiBoard);
        subject.Ads1115 = new Ads1115Configuration { I2cBus = i2cBus };

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject, isReserved ? [2, 3] : []);

        // Assert
        foreach (var pinNumber in new[] { 2, 3 })
        {
            Assert.Equal(isReserved ? "Used by I2C (ADS1115)" : null, subject.Pins[pinNumber].StatusMessage);
            Assert.Equal(isReserved ? 0 : 1, driver.GetOpenCount(pinNumber));
        }

        // Only the boards of the Raspberry Pi register driver start new pins with their power-on pull.
        Assert.Equal(isRaspberryPi ? GpioPinMode.InputPullDown : GpioPinMode.Input, subject.Pins[17].Mode);
    }

    [Fact]
    public async Task WhenAnOutputBecomesAPinOfAnAdc_ThenItIsClosedBeforeTheAdcIsCreated()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory();
        var subject = GpioTestSubject.Create(driver, adcFactory: adcFactory, pins: new Dictionary<int, GpioPin>
        {
            [12] = new() { Mode = GpioPinMode.Output }
        });
        subject.Mcp3008 = GpioTestSubject.CreateMcp3008Configuration();
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject, GpioTestSubject.Mcp3008PinNumbers);

        var events = new List<string>();
        driver.PinClosed = pinNumber =>
        {
            lock (events)
            {
                events.Add($"close {pinNumber}");
            }
        };
        adcFactory.EventRecorded = recordedEvent =>
        {
            lock (events)
            {
                events.Add(recordedEvent);
            }
        };

        // Act
        subject.Mcp3008 = GpioTestSubject.CreateMcp3008Configuration() with { ClockPin = 12 };
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Contains("dispose MCP3008 21", events);
        Assert.Contains("close 12", events);
        Assert.Contains("create MCP3008 12", events);
        var closeIndex = events.IndexOf("close 12");
        var createIndex = events.IndexOf("create MCP3008 12");
        Assert.True(closeIndex >= 0 && createIndex > closeIndex);
        Assert.Equal("Used by the MCP3008", subject.Pins[12].StatusMessage);
        Assert.Equal(ServiceStatus.Running, subject.Pins[21].Status);
    }

    [Fact]
    public async Task WhenControllerCannotBeCreated_ThenNoAdcIsCreated()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = CreateSubjectWithBothAdcs(new LatchingGpioDriver { IsUnavailable = true }, adcFactory);

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Error);
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, subject.Status);
        Assert.Empty(adcFactory.Events);
        Assert.Empty(subject.AnalogChannels);
    }

    [Fact]
    public async Task WhenMcp3008CannotBeCreated_ThenTheStatusReportsItAndTheNextApplyCreatesIt()
    {
        // Arrange
        var driver = new LatchingGpioDriver();
        var adcFactory = new RecordingAdcFactory { FailsNextMcp3008Creation = true };
        var subject = CreateSubjectWithBothAdcs(driver, adcFactory);
        await using var running = await GpioTestSubject.StartAsync(subject);
        await GpioTestSubject.WaitUntilAllPinsRunAsync(subject, AdcPinNumbers);
        Assert.Equal("MCP3008 init failed: No MCP3008 answered.", subject.StatusMessage);
        Assert.Equal(["create ADS1115 1"], adcFactory.Events);
        Assert.Equal(Enumerable.Range(8, 4), subject.AnalogChannels.Keys.Order());

        // Act
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["create ADS1115 1", "dispose ADS1115 1", "create MCP3008 21", "create ADS1115 1"], adcFactory.Events);
        Assert.Equal(12, subject.AnalogChannels.Count);
        Assert.Equal(ServiceStatus.Running, subject.Status);
        Assert.Null(subject.StatusMessage);
    }

    [Fact]
    public async Task WhenAdcIsCreatedAgain_ThenItsChannelsArePublishedWithAReading()
    {
        // Arrange
        var subject = GpioTestSubject.Create(new LatchingGpioDriver());
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };
        await using var running = await GpioTestSubject.StartAsync(subject);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.Status == ServiceStatus.Running);

        int[]? publishedRawValues = null;
        ((INotifyPropertyChanged)subject).PropertyChanged += (_, arguments) =>
        {
            if (arguments.PropertyName == nameof(GpioSubject.AnalogChannels))
            {
                publishedRawValues = subject.AnalogChannels.Values.Select(channel => channel.RawValue).ToArray();
            }
        };

        // Act
        await subject.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The fake reads all bits set, so a read channel reads -1.
        Assert.NotNull(publishedRawValues);
        Assert.Equal([-1, -1, -1, -1], publishedRawValues);
    }

    [Fact]
    public async Task WhenAds1115ChannelsAreRead_ThenTheSingleEndedInputsAreSelected()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = GpioTestSubject.Create(new LatchingGpioDriver(), adcFactory: adcFactory);
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };

        // Act
        await using var running = await GpioTestSubject.StartAsync(subject);

        // The fake reads all bits set, so a read channel reads -1.
        await AsyncTestHelpers.WaitUntilAsync(() =>
            subject.AnalogChannels.Count == 4 && subject.AnalogChannels.Values.All(channel => channel.RawValue == -1));

        // Assert
        Assert.Equal(
            [InputMultiplexer.AIN0, InputMultiplexer.AIN1, InputMultiplexer.AIN2, InputMultiplexer.AIN3],
            adcFactory.Ads1115InputMultiplexers.Distinct());
        Assert.All(subject.AnalogChannels.Values, channel => Assert.Equal(ServiceStatus.Running, channel.Status));
    }

    [Fact]
    public async Task WhenAdcIsDisposedDuringARead_ThenTheDisposalWaitsForTheRead()
    {
        // Arrange
        var adcFactory = new RecordingAdcFactory();
        var subject = GpioTestSubject.Create(new LatchingGpioDriver(), adcFactory: adcFactory);
        subject.Mcp3008 = GpioTestSubject.CreateMcp3008Configuration();

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

        // The read runs on the thread of the service loop, which the gate blocks for the short bound below.
        await using var running = await GpioTestSubject.StartAsync(subject);
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
        }
    }

    private static GpioSubject CreateSubjectWithBothAdcs(LatchingGpioDriver driver, RecordingAdcFactory adcFactory)
    {
        var subject = GpioTestSubject.Create(driver, isRaspberryPi: true, adcFactory);
        subject.Mcp3008 = GpioTestSubject.CreateMcp3008Configuration();
        subject.Ads1115 = new Ads1115Configuration { I2cBus = 1 };
        return subject;
    }

    private static void AssertAdcPinsAreReserved(GpioSubject subject, LatchingGpioDriver driver)
    {
        foreach (var pinNumber in AdcPinNumbers)
        {
            var pin = subject.Pins[pinNumber];
            Assert.Equal(pinNumber, pin.PinNumber);
            Assert.Equal(ServiceStatus.Unavailable, pin.Status);
            Assert.Equal(pinNumber is 2 or 3 ? "Used by I2C (ADS1115)" : "Used by the MCP3008", pin.StatusMessage);
            Assert.Null(pin.Controller);
            Assert.Equal(0, driver.GetOpenCount(pinNumber));
            Assert.Equal(0, driver.GetModeSetCount(pinNumber));
        }
    }
}
