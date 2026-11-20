using System.Device.Gpio;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
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

    private static (GpioSubject Subject, ConfigurableSubjectSerializer Serializer) CreateSubject(GpioDriver driver)
    {
        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(context);

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(GpioSubject).Assembly);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider());

        var subject = new GpioSubject(driver)
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

    private static Task WaitUntilAllPinsRunAsync(GpioSubject subject) =>
        AsyncTestHelpers.WaitUntilAsync(() =>
            subject.Status == ServiceStatus.Running &&
            subject.Pins.Count == 28 &&
            subject.Pins.Values.All(pin => pin.Status == ServiceStatus.Running));
}
