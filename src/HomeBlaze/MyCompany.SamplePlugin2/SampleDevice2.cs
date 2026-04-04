using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using MyCompany.Abstractions;
using Namotion.Interceptor.Attributes;

namespace MyCompany.SamplePlugin2;

[InterceptorSubject]
public partial class SampleDevice2 : BackgroundService, IConfigurable, ITitleProvider, IMyDevice
{
    private readonly Random _random = new();

    public SampleDevice2()
    {
        Name = "Sample Device 2";
        PollingIntervalMs = 3000;
        LightLevel = 500.0;
    }

    public string? Title => Name;

    // IMyDevice
    public string DeviceName => Name;
    public string DeviceType => "LightSensor";
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
