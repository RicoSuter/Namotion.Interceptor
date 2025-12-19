using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a Raspberry Pi GPIO controller with auto-discovered pins
/// and optional ADC support.
/// </summary>
[InterceptorSubject]
public partial class GpioSubject : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider
{
    /// <summary>
    /// Auto-discovered GPIO pins indexed by pin number.
    /// </summary>
    public partial Dictionary<int, GpioPin> Pins { get; set; }

    /// <summary>
    /// Optional MCP3008 ADC configuration.
    /// </summary>
    [Configuration]
    public partial Mcp3008Configuration? Mcp3008 { get; set; }

    /// <summary>
    /// Optional ADS1115 ADC configuration.
    /// </summary>
    [Configuration]
    public partial Ads1115Configuration? Ads1115 { get; set; }

    /// <summary>
    /// Analog channels from configured ADC (empty if no ADC configured).
    /// </summary>
    public partial Dictionary<int, AnalogChannel> AnalogChannels { get; set; }

    /// <inheritdoc />
    public string? Title => "GPIO";

    /// <inheritdoc />
    public string? Icon => "Memory";

    public GpioSubject()
    {
        Pins = new Dictionary<int, GpioPin>();
        AnalogChannels = new Dictionary<int, AnalogChannel>();
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // React to configuration changes - initialize/dispose ADC hardware
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Discover pins from hardware
        // 2. Register pin change callbacks
        // 3. Poll ADC channels if configured
        return Task.CompletedTask;
    }
}
