using System.Device.Gpio;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// Creates and runs GPIO subjects for tests. Disposing a started subject stops and disposes it.
/// </summary>
internal sealed class GpioTestSubject : IAsyncDisposable
{
    private GpioTestSubject(GpioSubject subject)
    {
        Subject = subject;
    }

    /// <summary>
    /// The pins of <see cref="CreateMcp3008Configuration"/>.
    /// </summary>
    public static int[] Mcp3008PinNumbers { get; } = [16, 19, 20, 21];

    public GpioSubject Subject { get; }

    /// <summary>
    /// Creates a subject on the given driver whose ADCs touch no hardware, polling once an hour so that a test reads
    /// the state the start or an apply left.
    /// </summary>
    public static GpioSubject Create(
        GpioDriver driver,
        bool isRaspberryPi = false,
        RecordingAdcFactory? adcFactory = null,
        Dictionary<int, GpioPin>? pins = null)
    {
        adcFactory ??= new RecordingAdcFactory();
        var subject = new GpioSubject(driver, isRaspberryPi, adcFactory.CreateMcp3008, adcFactory.CreateAds1115)
        {
            PollingInterval = TimeSpan.FromHours(1)
        };

        if (pins is not null)
        {
            subject.Pins = pins;
        }

        return subject;
    }

    /// <summary>
    /// Creates an MCP3008 configuration with clock pin 21 on <see cref="Mcp3008PinNumbers"/>.
    /// </summary>
    public static Mcp3008Configuration CreateMcp3008Configuration() =>
        new() { ClockPin = 21, MosiPin = 20, MisoPin = 19, ChipSelectPin = 16 };

    /// <summary>
    /// Creates the serializer that the storage reloads a changed file with, and gives the subject the context it uses.
    /// </summary>
    public static ConfigurableSubjectSerializer CreateSerializer(GpioSubject subject)
    {
        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(context);

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(GpioSubject).Assembly);
        ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        return new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider());
    }

    public static async Task<GpioTestSubject> StartAsync(GpioSubject subject)
    {
        await subject.StartAsync(CancellationToken.None);
        return new GpioTestSubject(subject);
    }

    /// <summary>
    /// Waits until the subject runs with 28 pins that all run, except the given ones, which are unavailable.
    /// </summary>
    public static Task WaitUntilAllPinsRunAsync(GpioSubject subject, params int[] unavailablePinNumbers) =>
        AsyncTestHelpers.WaitUntilAsync(() =>
            subject.Status == ServiceStatus.Running &&
            subject.Pins.Count == 28 &&
            subject.Pins.All(entry => entry.Value.Status ==
                (unavailablePinNumbers.Contains(entry.Key) ? ServiceStatus.Unavailable : ServiceStatus.Running)));

    public async ValueTask DisposeAsync()
    {
        await Subject.StopAsync(CancellationToken.None);
        Subject.Dispose();
    }
}
