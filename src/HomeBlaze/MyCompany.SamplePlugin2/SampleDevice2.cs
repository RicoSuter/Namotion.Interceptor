using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using MyCompany.Abstractions;
using System.ComponentModel;
using Namotion.Interceptor.Attributes;

namespace MyCompany.SamplePlugin2;

[InterceptorSubject]
[Category("Devices")]
[Description("A sample light sensor plugin that simulates ambient light and UV index readings.")]
public partial class SampleDevice2 : BackgroundService, IConfigurable, ITitleProvider, IMyDevice
{
    private readonly Random _random = new();

    public SampleDevice2()
    {
        Name = "Sample Device 2";
        PollingIntervalMs = 3000;
        LightLevel = 500.0;
    }

    [Derived]
    public string? Title => Name;

    // IMyDevice
    [Derived]
    public string DeviceName => Name;
    public string DeviceType => "LightSensor";
    [Derived]
    public double? CurrentValue => LightLevel;

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial int PollingIntervalMs { get; set; }

    [State]
    public partial double LightLevel { get; internal set; }

    [Derived]
    public bool IsDark => LightLevel < 100;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            LightLevel = Math.Clamp(LightLevel + _random.NextDouble() * 20 - 10, 0, 1000);
            await Task.Delay(PollingIntervalMs, stoppingToken);
        }
    }
}
